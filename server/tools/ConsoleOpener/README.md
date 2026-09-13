# ConsoleOpener — opening the August client's debug console (Door B)

`tools/ConsoleOpener` opens the H1Z1 KOTK **August 2017** client's own debug console so Cranberry's
developer console / Jiggy-style mod menu can be typed into it. It is a re-derivation, for build
**0.0.118.208059** (`ClientProtocol_1148`), of the owner's own 1087 tool
(`C:\Z1\Server\tools\ToggleConsole`): same idea, same F8 habit, every address freshly read out of the
August binary — none of them ported, because the two clients share nothing at these offsets.

Design: `C:\Aug2017\out\devconsole-20260901\DESIGN-dev-console.md` §3 (this is Lane D).
Evidence: `R5-hash-and-gate.md` §2, `refute-1.md` §3.

> **Try Door A first.** The console has two doors and the other one needs no tooling at all: the
> server sets `SelfRecord.FlagI` (self-record field 122 = the client's `+0x106b9` byte) for an Owner
> session, and the client's own **Tilde (`)** binding then passes the same permission gate this tool
> patches. Start the host with `CRANBERRY_CONSOLE_SELF_FLAG=1`, log in, press Tilde. Nothing to
> install, nothing for BattlEye to look at. Only come here if that disappoints.

---

## 1. Build and run

```
dotnet build C:\Aug2017\Server\tools\ConsoleOpener        # standalone; NOT part of Cranberry.slnx
dotnet test  C:\Aug2017\Server\tests\ConsoleOpener.Tests  # 19 byte-level guards
```

The tool is deliberately **not** a member of `Cranberry.slnx`: a process-patching tool has no
business inside the server build (design §3.4). `dotnet build Cranberry.slnx` never compiles it.

| Mode | Command | Does |
|---|---|---|
| resident hotkey (**what you want**) | `powershell -File tools\ConsoleOpener\start-opener.ps1`, or `dotnet run --project tools\ConsoleOpener -- --watch` | waits for `H1Z1.exe`, verifies the build, patches the gate in memory, allocates the stub page, then polls the hotkey every 20 ms and toggles the console on each press. Re-attaches on every client relaunch. One instance at a time |
| one-shot | `dotnet run --project tools\ConsoleOpener` | patch (if needed) and toggle once |
| check only | `… -- --verify` | prints the build verdict and the current bytes at every site. **Changes nothing** — safe to run at any time, including mid-session |
| undo | `… -- --restore` | writes every patched site back to its shipped bytes, after reading back exactly the bytes this tool writes |

Other switches: `--key 2D` (Insert; default `77` = F8, `24` Home, `78` F9) · `--pid <n>` (the module
name is still checked) · `--log <path>` (append-only status file) · `--toggle-arg <int>` ·
`--no-spectate-packet` · `--any-build` (refused on its own, see §5).

Exit codes: **0** ok · **1** refused or failed · **2** bad usage.

Run it in an **Administrator** window: `OpenProcess` on the client needs it. Without elevation the
watcher logs `run this window as Administrator` and retries every three seconds rather than dying.

---

## 2. The owner's recipe (design §6.1 step 3)

1. Start the Cranberry host as usual and read its `console: ON …` boot line.
2. **Door A first**, if you have not tried it: host with `CRANBERRY_CONSOLE_SELF_FLAG=1`, log in,
   press **Tilde**. A *visible pane* is the only success — see the caveat in §6.
3. Door B, in an **Administrator** PowerShell:

   ```
   dotnet run --project C:\Aug2017\Server\tools\ConsoleOpener -- --verify
   ```

   with the client running. It must print `build verified: H1Z1 KotK August 2017 (0.0.118.208059,
   ClientProtocol_1148)` and change nothing. Then:

   ```
   powershell -File C:\Aug2017\Server\tools\ConsoleOpener\start-opener.ps1
   ```

   The log must show

   ```
   PATCH OK — gate opened at 0x…291d8c (0x75 -> 0xEB).
   READY — attached to pid …, build verified: … Press key 0x77 in game to open the console.
   ```

4. Press **F8** in game. Then type `/commands`, `/m`, `/where` — see design §6.1 steps 4-5.
5. Every F8 also makes the client send `Command.Spectate "ObserverCamera"` to the server; Cranberry
   logs that at Debug and ignores it. **That line in the host log is expected** (§4).

**If no pane appears:** run `--verify` again, read the log for `REFUSING` / `UNEXPECTED`, and check
`C:\Aug2017\Client\Logs` for `BEClient` messages — that is the BattlEye answer this project does not
have yet (§7). **If the client crashes:** keep the opener log and `Client\Logs\H1Z1.log`, and do
**not** retry with `--any-build`.

---

## 3. The patch table — every byte read out of this binary

Target function: `FUN_141291d50`, the console **toggle-command**
(`out\ghidra-aug\devconsole-ToggleDebugConsole\`). It loads the game and self globals itself
(`DAT_143f696a0`, `DAT_143f69f60`), so a bare remote thread can call it as
`(rcx = unused, rdx = char* commandLine)`. Its sibling `FUN_140b89400` is the cleaner opener but takes
the game object in `rcx`, so it is the Tilde-key path, not a remote-thread target — that one is
Door A's.

`.text` maps RVA `0x1000` to file offset `0x600`, so **file = VA − `0x140000A00`**.

| Site | RVA | VA | file | bytes | written to | evidence |
|---|---|---|---|---|---|---|
| toggle entry | `0x1291d50` | `0x141291d50` | `0x1291350` | `40 55 56 57 41 56 41 57` | **never** — verified in memory before every fire | R5 §2.1/§2.3 |
| gate `jne` | `0x1291d8c` | `0x141291d8c` | `0x129138c` | `75 1d` → **`eb 1d`** (one byte written) | always | R5 §2.2 |
| `Command.Spectate` `call` | `0x1291faf` | `0x141291faf` | `0x12915af` | `e8 71 b3 da fe` → `90 90 90 90 90` | **only with `--no-spectate-packet`** | design §1.5/§3.2 |

The gate, in the disassembly:

```
141291d84  call [rax+0x98]             ; admin-cvar check -> al
141291d8c  jne  141291dab              ; <-- the gate: admin -> proceed
141291d8e  mov  rax,[143f69f60]        ; else the self player object
141291d98  je   14129201b              ;   null -> return
141291d9e  cmp  byte [rax+0x106b9],0   ; SelfRecord.FlagI
141291da5  je   14129201b              ;   0 -> return, console stays shut
141291dab  ...                         ; proceed: fire EVENT_TOGGLE_DEBUG_CONSOLE
```

`75 → eb` turns the conditional jump into an unconditional one, so the "admin" branch is taken every
time and the console opens without the flag and without the admin cvar. **One byte**, and a one-byte
write is atomic — the tool verifies the whole two-byte instruction but writes only the opcode.

The stub fired by `CreateRemoteThread`, in a 256-byte RWX page (`[0x00]` the NUL-terminated string,
`[0x10]` the code):

```
sub rsp,0x28 ; xor rcx,rcx ; mov rdx,pageVa ; mov rax,toggleVa ; call rax ; add rsp,0x28 ; ret
```

`0x28` is 0x20 of shadow space plus the 8 that keeps the ABI's 16-byte stack alignment. All of this
is pinned byte-for-byte in `RemotePageTests`.

---

## 4. The `Command.Spectate` packet, and why the NOP is opt-in

`FUN_141291d50:100-104` builds `{level 9, sub 0x510, "ObserverCamera"}` and hands it to the c2s
sender `FUN_141265760` — so **every toggle sends `Command.Spectate 09 0x510 "ObserverCamera"` to the
server**. On retail that is what flipped the player into spectator/free-cam, because the retail server
answered it. On 1087 the owner had to NOP it.

**Cranberry never answers it.** The zone hook matches the packet and writes one Debug line
(`console: client sent Command.Spectate "ObserverCamera" (console toggle) — ignored`, design §1.5), so
against our own server the packet is inert. NOPping the call therefore buys nothing and costs one more
write for BattlEye to notice — hence `--no-spectate-packet` is **opt-in**, and the default is one
write fewer.

Five bytes is not an atomic write, so the owner's 2026-08-23 rule applies: *never mutate a hot CALL*.
This one is cold — `FUN_141291d50` runs only when the console toggles, which is exactly when the tool
is not writing. `PatchSitesTests.OnlyOneSiteTakesAMultiByteWriteAndItIsOptIn` pins that.

The Tilde key path (`FUN_140b89400`) sends nothing at all, which is one more reason to try Door A
first.

---

## 5. What it refuses to do

* **A binary it does not recognise.** File size **and** SHA-256 (`72,818,304` bytes,
  `d949d39f…29dd`) are checked before a single byte is written, cached per path/size/mtime.
  `--any-build` alone is refused **loudly**; it needs `--i-re-derived-the-rvas` beside it, and you
  should only type that after re-deriving every RVA against the new binary and adding its row to
  `BuildGate.KnownBuilds`. Writing `0xEB` into the middle of another build's instruction is how you
  get a client that crashes in a way nobody can debug.
* **A process that is not H1Z1.** The main module name is checked even when `--pid` pins one.
* **A site whose bytes are not what the shipped binary holds.** Every site is inspected *before the
  first write*, so a wrong second site aborts the run with the client untouched. If the optional
  five-byte write still fails after the gate was written, the gate is put back — a half-patched
  client is the one state nobody can diagnose later.
* **Restoring bytes it did not write.** `--restore` writes a site back only when it reads back exactly
  this tool's patched bytes; anything else is reported and left alone.
* **Writing the disk image.** `C:\Aug2017\Client\H1Z1.exe` is opened **read-only**, to hash it, and
  never written — on-disk patching is refused by BattlEye on this client class. Everything else is
  in-memory (`OpenProcess` / `VirtualProtectEx` / `WriteProcessMemory` / `CreateRemoteThread`).
* **Firing into a function that moved.** The eight-byte prologue of `FUN_141291d50` is read out of the
  live process before every remote thread. The disk hash proves which file was loaded; this proves
  nothing has moved in memory since.
* **Reading client globals it does not need.** `--verify` reads two code sites and nothing else. The
  UI event sink `DAT_1451d2650` is a "the UI is up" precondition, not a gate, and is deliberately not
  consulted.
* **Deleting a log.** The `--log` file is appended to, never truncated, never rotated.

`--toggle-arg <int>` is the one mode that does not do what it sounds like: `FUN_141291d50:58-85`
tokenises its argument, skips a leading `version` token, parses the next token as an int and stores a
positive value at `game+0x64e9`. It **cannot type a console line** — the mode exists only to reproduce
that store.

---

## 6. Caveat for the first click: a script init is not a door

`FUN_140b89400:15-16` runs the Lua `Console:StartDebugConsole` initialiser **before** the gate
whenever `client+0x327f9 == 0`. So the first Tilde press initialises the console script even when
`FlagI` is false, and something may appear to happen without the console being open.

**Only a visible pane counts as success**, for either door. Log what you actually see.

---

## 7. What is still unknown

| Question | Mark | Closed by |
|---|---|---|
| Does BattlEye on 208059 tolerate the in-memory patch and the remote thread? It did on 1087; it has never been attempted here | **[U]** | the first F8 |
| Does the remote-thread toggle open the pane cleanly (first person kept, no side effects)? | **[U]** | the first F8 |
| Does `SelfRecord.FlagI = true` open the console on Tilde, and does it light anything else? | **[U]** | Door A, design §6.1 step 2 |

The three byte rows and the build identity are **[P]** — read out of `H1Z1.exe` on 2026-09-02 by R5,
by the design lane, by refute-1, and once more by the lane that wrote this tool. `PatchSitesTests`
re-reads them from the exe on every test run, so the table cannot drift from the binary unnoticed.

---

## 8. Files

```
tools/ConsoleOpener/
  ConsoleOpener.csproj   net10.0 exe, no packages, no unsafe blocks
  Program.cs             argument parsing, the four modes, exit codes
  BuildGate.cs           KnownBuilds (one row), size + SHA-256 verdict with a cache
  PatchSites.cs          the RVA/byte table and the remote-page builder — pure data, fully tested
  ClientProcess.cs       find/open the client, read/write/protect, inspect, patch, restore, fire
  HotkeyWatcher.cs       the GetAsyncKeyState edge-detect loop, single-instance mutex, re-attach
  StatusLog.cs           console + append-only status file
  start-opener.ps1       the elevated one-liner the owner runs
  README.md              this file
tests/ConsoleOpener.Tests/
  PatchSitesTests.cs     the table vs the bytes in H1Z1.exe, write widths, instruction lengths
  RemotePageTests.cs     the stub, byte for byte, and the string that precedes it
  BuildGateTests.cs      the known build, refusals, and the loud --any-build allow
```
