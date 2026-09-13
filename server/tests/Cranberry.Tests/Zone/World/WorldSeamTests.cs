using System.Reflection;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// docs/22 §9.4: the simulation holds no transport type and reads no wall clock. Until the code can
/// move into its own socket-free assembly (migration step 9), this test is the compiler.
/// </summary>
public sealed class WorldSeamTests
{
    private const string Namespace = "Cranberry.Zone.World";

    private static IEnumerable<Type> WorldTypes() =>
        typeof(Match).Assembly.GetTypes().Where(type => type.Namespace == Namespace);

    private static bool IsTransportType(Type? type)
    {
        while (type is not null)
        {
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                type = type.GetElementType();
                continue;
            }

            string? space = type.Namespace;
            if (space is not null && (space.StartsWith("Cranberry.Transport", StringComparison.Ordinal)
                || space.StartsWith("System.Net", StringComparison.Ordinal)))
            {
                return true;
            }

            if (type.IsGenericType && type.GetGenericArguments().Any(IsTransportType))
            {
                return true;
            }

            return false;
        }

        return false;
    }

    [Fact]
    public void OnlyTheSessionBridgeNamesATransportType()
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();
        foreach (Type type in WorldTypes())
        {
            foreach (FieldInfo field in type.GetFields(Flags))
            {
                if (IsTransportType(field.FieldType))
                {
                    offenders.Add($"{type.Name}.{field.Name}");
                }
            }

            foreach (PropertyInfo property in type.GetProperties(Flags))
            {
                if (IsTransportType(property.PropertyType))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }

            foreach (MethodBase method in type.GetMethods(Flags).Cast<MethodBase>().Concat(type.GetConstructors(Flags)))
            {
                if (method is MethodInfo info && IsTransportType(info.ReturnType))
                {
                    offenders.Add($"{type.Name}.{method.Name}()");
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (IsTransportType(parameter.ParameterType))
                    {
                        offenders.Add($"{type.Name}.{method.Name}({parameter.Name})");
                    }
                }
            }
        }

        Assert.All(offenders, offender =>
            Assert.StartsWith(nameof(SessionBridge), offender, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSeamTypeItselfStillExistsAndIsTheOnlyOne()
    {
        // A regression guard on the test above: if SessionBridge stopped naming a transport type the
        // assertion would pass vacuously.
        Assert.Contains(typeof(SessionBridge).GetProperties(), property => IsTransportType(property.PropertyType));
    }

    [Fact]
    public void NoTypeUnderWorldReadsAWallClock()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(SourceDirectory(), "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (string forbidden in new[] { "DateTime", "TickCount64", "Stopwatch", "Environment." })
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {forbidden}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void EverySystemLivesInTheWorldNamespaceAndImplementsISystem()
    {
        Type[] systems = WorldTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type.Name.EndsWith("System", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(systems);
        Assert.All(systems, system => Assert.True(typeof(ISystem).IsAssignableFrom(system), system.Name));
    }

    private static string SourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cranberry.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string source = Path.Combine(directory.FullName, "src", "Cranberry.Zone", "World");
        Assert.True(Directory.Exists(source), $"World sources not found at {source}.");
        return source;
    }
}
