using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The dispatcher: one <c>Command.ExecuteCommand</c> in, some console lines out.
/// <para>
/// It is pure in the sense that matters - it knows nothing about <c>ZoneService</c>,
/// <c>SoeConnection</c> or the gateway. Everything it can reach outside itself arrives as a
/// delegate on <see cref="ConsoleContext"/>, which is what lets the whole menu, the parser, the
/// gates and the renderer be tested without a client, a socket or a match (design §4.1, §7 Lane B).
/// </para>
/// <para>
/// <b>Never silent for development accounts.</b> Those paths answer with at least one line: an unknown hash, a refused tier,
/// a phase gate, a command that returned nothing. The one deliberate exception is the rate limit,
/// which drops an input that arrived inside <see cref="ConsoleOptions.RateLimitMs"/> of the last
/// one - a held key must not become a packet storm, and the owner will simply press again.
/// Player accounts have no development surface: denied debug inputs are silent, while their
/// hosted-game command remains available through ordinary chat.
/// </para>
/// </summary>
public sealed class ConsoleEngine
{
    private readonly ITransportLog? _log;
    private readonly Func<long> _clock;

    private ConsoleEngine(ConsoleOptions options, CommandRegistry registry, ITransportLog? log, Func<long> clock)
    {
        Options = options;
        Registry = registry;
        Tree = MenuTree.Build(registry);
        _log = log;
        _clock = clock;
    }

    /// <summary>The switches this engine was built with.</summary>
    public ConsoleOptions Options { get; }

    /// <summary>Every command, and the collision oracle that let them be registered.</summary>
    public CommandRegistry Registry { get; }

    /// <summary>The menu of design §2.5, built over <see cref="Registry"/>.</summary>
    public MenuTree Tree { get; }

    /// <summary>
    /// Where the refused-name backstop comes from. Lane C's <c>ClientCollisionLog</c> sets this to
    /// a reader over <c>Client\Logs\AdminCommands.log</c>; unset, <see cref="RefreshRefusals"/> is
    /// a no-op and the registry's own start-up collision test is the only guard (design §4.3).
    /// </summary>
    public Func<IEnumerable<string>>? RefusalSource { get; set; }

    /// <summary>The registry half of the boot banner.</summary>
    public string Summary => Registry.Summary;

    /// <summary>The line the first burst prints in the pane.</summary>
    public const string ReadyLine = "* Cranberry console ready -- /commands lists development commands";

    /// <summary>Builds an engine. <paramref name="clock"/> exists so tests own the rate limit.</summary>
    public static ConsoleEngine Create(
        ConsoleOptions options,
        CommandRegistry registry,
        ITransportLog? log = null,
        Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        return new ConsoleEngine(options, registry, log, clock ?? (() => System.Environment.TickCount64));
    }

    /// <summary>Runs one typed line that arrived on the wire.</summary>
    public void Execute(ConsoleContext ctx, ConsoleSession session, ExecuteCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        if (!Accept(session))
        {
            return;
        }

        ConsoleCommand? command = Registry.ByHash(request.Hash);
        if (!ConsoleAccessPolicy.Allows(session.Tier, command?.Name, request.Arguments)) return;
        IConsoleSurface surface = SurfaceOf(ctx, session);

        if (command is null)
        {
            // The client turns every unregistered name into hash("HELP") with empty arguments, so
            // a hash we do not know is either a name we never pushed or a build mismatch. Say so
            // rather than dropping it (R1 §5 item 9).
            surface.Line($"? unknown command 0x{request.Hash:x8} -- /commands lists everything");
            ctx.Info($"console: unknown hash 0x{request.Hash:x8} args=[{request.Arguments}]");
            return;
        }

        string typed = Registry.NameOf(request.Hash) ?? command.Name;
        bool catchAll = request.Hash == CommandHash.Help && request.Arguments.Length == 0;
        Dispatch(ctx, session, surface, command, typed, request.Arguments, fromMenu: false, catchAll);
    }

    /// <summary>
    /// Runs one line as if it had been typed. The menu uses it for every leaf, which is the
    /// "one engine, two front doors" rule; tests use it to skip building a packet.
    /// </summary>
    public void ExecuteLine(ConsoleContext ctx, ConsoleSession session, string name, string arguments = "")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(name);

        ConsoleCommand? command = Registry.ByName(name);
        if (!ConsoleAccessPolicy.Allows(session.Tier, command?.Name, arguments)) return;
        IConsoleSurface surface = SurfaceOf(ctx, session);
        if (command is null)
        {
            surface.Line($"? no such command: /{name}");
            return;
        }

        Dispatch(ctx, session, surface, command, name, arguments, fromMenu: false, catchAll: false);
    }

    private void Dispatch(
        ConsoleContext ctx,
        ConsoleSession session,
        IConsoleSurface surface,
        ConsoleCommand command,
        string typed,
        string arguments,
        bool fromMenu,
        bool catchAll)
    {
        CommandLine line = CommandLine.Parse(typed, arguments, command.KeepCase);

        if (Options.ModMenuEnabled && MenuInput.IsMenuName(typed))
        {
            Steer(ctx, session, surface, MenuInput.Parse(typed, line));
            return;
        }

        IReadOnlyList<string> lines = RunGated(ctx, session, command, line, fromMenu, catchAll);
        foreach (string reply in lines)
        {
            surface.Line(ConsoleReply.NonEmpty(reply));
        }

        string loggedArguments = command.SensitiveArguments ? "redacted" : arguments;
        ctx.Info(
            $"console: /{typed} args=[{loggedArguments}] tier={session.Tier} -> {lines.Count} line(s) "
            + $"({ConsoleOptions.SurfaceWord(session.SurfaceKind(Options))})");
    }

    /// <summary>Tier gate, phase gate, not-yet gate, then the body. The reply is never empty.</summary>
    private IReadOnlyList<string> RunGated(
        ConsoleContext ctx,
        ConsoleSession session,
        ConsoleCommand command,
        CommandLine line,
        bool fromMenu,
        bool catchAll = false)
    {
        if (command.Tier > session.Tier)
        {
            string refusal = command.Tier == ConsoleTier.Owner
                ? $"! Owner only (you are {session.Tier})"
                : $"! Tester or above (you are {session.Tier})";
            ctx.Info($"console: REFUSED /{command.Name} -- {refusal[2..]}");
            return [refusal];
        }

        if (!MatchGates.Allows(command.Gate, ctx.Step()))
        {
            return [$"! {MatchGates.Refusal(command.Gate, ctx.Step())}"];
        }

        if (command.NotYet is not null)
        {
            return [$"- not available yet -- {command.NotYet}"];
        }

        if (command.Name == "help" || command.Name == "commands")
        {
            return HelpFormatter.Render(Registry, line, catchAll);
        }

        ConsoleReply reply = command.Run(new CommandCall(ctx, command, line, session, fromMenu));
        if (reply.SuppressOutput) return Array.Empty<string>();
        if (reply.Lines.Count == 0)
        {
            return [$"? /{command.Name} ran; details in the host log"];
        }

        return reply.Lines;
    }

    private void Steer(ConsoleContext ctx, ConsoleSession session, IConsoleSurface surface, MenuInput input)
    {
        bool wasOpen = session.Menu.IsOpen;
        MenuView view = BuildView(ctx, session);
        MenuStep step = session.Menu.Apply(input, Tree, view);
        session.Menu = step.State;

        if (step.Setting is not null)
        {
            surface.Line(ApplySetting(session, step.Setting));
        }

        if (step.ResultLine is not null)
        {
            surface.Line(ConsoleReply.NonEmpty(step.ResultLine));
        }

        if (step.Invocation is not null)
        {
            ConsoleCommand? command = Registry.ByName(step.Invocation.Name);
            if (command is null)
            {
                surface.Line($"- /{step.Invocation.Name} is not registered on this build");
            }
            else
            {
                CommandLine line = CommandLine.Parse(command.Name, step.Invocation.Arguments, command.KeepCase);
                foreach (string reply in RunGated(ctx, session, command, line, fromMenu: true))
                {
                    surface.Line(ConsoleReply.NonEmpty(reply));
                }

                string loggedInvocation = command.SensitiveArguments
                    ? $"/{command.Name} [redacted]" : step.Invocation.Typed;
                ctx.Info($"console: menu {step.Invocation.NodeId} -> {loggedInvocation} tier={session.Tier}");
            }
        }

        if (wasOpen && !session.Menu.IsOpen)
        {
            string ticker = session.StatusTicker();
            if (ticker.Length > 0)
            {
                surface.Line($"* {ticker}");
            }
        }

        if (step.Redraw && session.Menu.IsOpen)
        {
            Draw(ctx, session, surface);
        }
    }

    /// <summary>Renders the frame and sends it, one line per packet, suppressing an identical redraw.</summary>
    private void Draw(ConsoleContext ctx, ConsoleSession session, IConsoleSurface surface)
    {
        IReadOnlyList<string> frame = FrameRenderer.Render(session.Menu, Tree, BuildView(ctx, session));
        if (frame.Count == 0)
        {
            return;
        }

        int hash = string.Join('\n', frame).GetHashCode(StringComparison.Ordinal);
        if (session.Settings.SuppressIdenticalFrames && hash == session.Menu.LastFrameHash)
        {
            return;
        }

        session.Menu = session.Menu with { LastFrameHash = hash };
        foreach (string row in frame)
        {
            surface.Line(ConsoleReply.NonEmpty(row));
        }
    }

    /// <summary>
    /// Sends the <c>Command.AddWorldCommand</c> burst and, once per link, the ready line.
    /// Synchronous by contract: the caller is inside the <c>ClientIsReady</c> arm, and anything
    /// deferred would land after the zoning label list the bootstrap tests pin (design §1.2, §4.6).
    /// </summary>
    public void OnClientIsReady(ConsoleContext ctx, ConsoleSession session, bool zoning)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(session);

        if (zoning)
        {
            session.Menu = session.Menu.CloseKeepingCursors();
        }

        bool send = Options.Register switch
        {
            ConsoleRegisterPolicy.EachZone => true,
            ConsoleRegisterPolicy.Once => session.BurstsSent == 0,
            ConsoleRegisterPolicy.Zoning => zoning,
            _ => false,
        };

        if (!send)
        {
            return;
        }

        bool development = ConsoleAccessPolicy.HasDevelopmentAccess(session.Tier);
        IReadOnlyList<string> names = development ? Registry.Names
            : [.. Registry.Names.Where(ConsoleAccessPolicy.IsPlayerCommand)];
        if (ctx.Send is { } send1)
        {
            foreach (string name in names)
            {
                AddWorldCommand packet = new(name);
                send1(packet.WriteTo);
            }
        }

        session.BurstsSent++;
        ctx.Info(
            $"console: pushed {names.Count} AddWorldCommand names "
            + $"({ConsoleOptions.RegisterWord(Options.Register)}, burst {session.BurstsSent})");

        if (development && !session.ReadyLineSent)
        {
            session.ReadyLineSent = true;
            SurfaceOf(ctx, session).Line(ReadyLine);
        }
    }

    /// <summary>
    /// Re-reads the client's refusal log through <see cref="RefusalSource"/> and drops any name it
    /// names from the next burst. No timer and no background read: it happens on the next inbound
    /// <c>09 42</c>, which is the only moment the answer can matter.
    /// </summary>
    public void RefreshRefusals(ConsoleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (RefusalSource is null)
        {
            return;
        }

        foreach (string name in RefusalSource())
        {
            if (Registry.MarkRefused(name))
            {
                ctx.Trouble(
                    $"console: client refused '{name}' -- rename it (docs/97 §7); its menu leaf is greyed");
                _log?.Warn($"console: client refused '{name}'");
            }
        }
    }

    private bool Accept(ConsoleSession session)
    {
        long now = _clock();
        if (session.LastInputTick != long.MinValue && now - session.LastInputTick < Options.RateLimitMs)
        {
            return false;
        }

        session.LastInputTick = now;
        return true;
    }

    private IConsoleSurface SurfaceOf(ConsoleContext ctx, ConsoleSession session)
    {
        ConsoleSurfaceKind kind = session.SurfaceKind(Options);
        return ctx.SurfaceFor?.Invoke(kind) ?? ctx.Surface;
    }

    /// <summary>Builds the live view the renderer and the state machine read.</summary>
    public MenuView BuildView(ConsoleContext ctx, ConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(session);
        return new MenuView
        {
            Settings = session.Settings,
            Tier = session.Tier,
            MatchStep = ctx.Step(),
            ServerVersion = ctx.ServerVersion,
            ClientBuild = ctx.ClientBuild,
            Toggle = node => ctx.RowToggle?.Invoke(node.Id) ?? DefaultToggle(session, node),
            Value = node => ValueOfRow(ctx, session, node),
            Note = node => ctx.RowNote?.Invoke(node.Id),
        };
    }

    private static bool DefaultToggle(ConsoleSession session, MenuNode node)
    {
        if (node.SettingKey is { } key)
        {
            ConsoleSettings s = session.Settings;
            return key switch
            {
                "keys" => s.HintKeys,
                "typed" => s.TypedHints,
                "confirm" => s.Confirms,
                "wrap" => s.WrapAround,
                "suppress" => s.SuppressIdenticalFrames,
                "remember" => s.RememberPath,
                "live" => s.LiveRefresh,
                "showall" => s.ShowAllRows,
                _ => false,
            };
        }

        return node.Id == "player.god" && session.Invulnerable;
    }

    private string? ValueOfRow(ConsoleContext ctx, ConsoleSession session, MenuNode node)
    {
        if (node.SettingKey is { } key)
        {
            ConsoleSettings s = session.Settings;
            return key switch
            {
                "rows" => s.Rows.ToString(),
                "width" => s.Width.ToString(),
                "theme" => s.Theme == MenuTheme.Heavy ? "heavy" : "plain",
                "surface" => ConsoleOptions.SurfaceWord(session.SurfaceKind(Options)),
                _ => null,
            };
        }

        return ctx.RowValue?.Invoke(node.Id);
    }

    private string ApplySetting(ConsoleSession session, SettingChange change)
    {
        ConsoleSettings s = session.Settings;
        bool on = change.Value == "on";

        switch (change.Key)
        {
            case "rows":
                session.Settings = s with
                {
                    Rows = Math.Clamp(
                        int.TryParse(change.Value, out int rows) ? rows : s.Rows,
                        ConsoleOptions.MinimumRows,
                        ConsoleOptions.MaximumRows),
                };
                return $"+ Rows {session.Settings.Rows}";

            case "width":
                session.Settings = s with
                {
                    Width = Math.Clamp(
                        int.TryParse(change.Value, out int width) ? width : s.Width,
                        ConsoleOptions.MinimumWidth,
                        ConsoleOptions.MaximumWidth),
                };
                return $"+ Width {session.Settings.Width}";

            case "keys":
                session.Settings = s with { HintKeys = on };
                return $"+ Hint keys [{OnOff(on)}]";

            case "typed":
                session.Settings = s with { TypedHints = on };
                return $"+ Typed hints [{OnOff(on)}]";

            case "confirm":
                session.Settings = s with { Confirms = on };
                return $"+ Confirms [{OnOff(on)}]";

            case "wrap":
                session.Settings = s with { WrapAround = on };
                return $"+ Wrap around [{OnOff(on)}]";

            case "suppress":
                session.Settings = s with { SuppressIdenticalFrames = on };
                return $"+ Frame suppression [{OnOff(on)}]";

            case "remember":
                session.Settings = s with { RememberPath = on };
                return $"+ Remember path [{OnOff(on)}]";

            case "live":
                session.Settings = s with { LiveRefresh = on };
                return $"+ Live refresh [{OnOff(on)}]";

            case "showall":
                session.Settings = s with { ShowAllRows = on };
                return $"+ Show all rows [{OnOff(on)}]";

            case "theme":
                session.Settings = s with { Theme = change.Value == "heavy" ? MenuTheme.Heavy : MenuTheme.Plain };
                return $"+ Theme {change.Value}";

            case "surface":
                session.Surface = ConsoleOptions.ParseSurface(change.Value, Options.Surface);
                return $"+ Surface {ConsoleOptions.SurfaceWord(session.Surface.Value)} "
                    + $"({ConsoleOptions.SurfaceOpcode(session.Surface.Value)})";

            default:
                return $"? no such setting: {change.Key}";
        }
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";
}
