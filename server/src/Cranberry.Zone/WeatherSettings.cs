using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// The client's weather/sky struct as parsed by <c>FUN_140a482b0</c> (21 floats, one string, 12
/// floats — 137 bytes with an empty string). The same struct rides inside <c>SendZoneDetails</c>,
/// <c>UpdateWeatherData</c> (0xca) and <c>ClientBeginZoning</c>; only 0xca arms the sky blend
/// (<c>FUN_140b88950 → FUN_142483760</c>). Field names are the client's own: the hash table at
/// <c>0x143efca2c</c> (StringHash of the strings at <c>0x1430ed9f0</c>) resolved to struct offsets by
/// <c>FUN_142481b80</c>; client defaults from the struct constructor <c>FUN_140a7c6d0</c>.
/// </summary>
/// <remarks>
/// Four values must stay positive: <see cref="TransitionTime"/> (the blend divides by it) and the three
/// fog values (<see cref="FogDensity"/>, <see cref="FogFloor"/>, <see cref="FogGradient"/> are lerped
/// as <c>expf(lerp(logf(from), logf(to), t))</c> in <c>FUN_142482ee0</c>; <c>logf(0)</c> turns the fog
/// shader constant 0x7e7e623f into NaN and the whole frame black — the all-zero struct of the first
/// runs did exactly that).
/// </remarks>
public sealed record WeatherSettings
{
    // wire 0-3
    public float TransitionTime { get; init; } = 1f;
    // Exact float bits of the only click-proven fog triple (0x3935C0CE ≈ 1.7334e-4).
    public float FogDensity { get; init; } = BitConverter.Int32BitsToSingle(0x3935C0CE);
    public float FogFloor { get; init; } = 10f;
    public float FogGradient { get; init; } = 0.0144f;

    // wire 4-6
    public float GlobalPrecipitation { get; init; }
    public float Temperature { get; init; } = 75f;
    public float Overcast { get; init; }

    // wire 7-11
    public float CloudWeight0 { get; init; }
    public float CloudWeight1 { get; init; }
    public float CloudWeight2 { get; init; }
    public float CloudWeight3 { get; init; }
    public float CloudShadows { get; init; }

    // wire 12-14 (degrees; the sun vector is R(hours·2π/24)·R(SunAxisX)·R(SunAxisY+90), FUN_142483c50)
    public float SunAxisX { get; init; } = 45f;
    public float SunAxisY { get; init; }
    public float SunAxisZ { get; init; }

    // wire 15-18
    public float WindDirX { get; init; } = -1f;
    public float WindDirY { get; init; } = -0.05f;
    public float WindDirZ { get; init; } = -1f;
    public float WindSpeed { get; init; } = 3f;

    // wire 19-20
    public float RainMinStrength { get; init; }
    public float RainRampUpTimeSeconds { get; init; }

    /// <summary>Cloud density texture (i32 len + bytes); loaded by <c>FUN_142481740</c>, empty is tolerated.</summary>
    public string CloudDensityTexture { get; init; } = "sky_Z_clouds.dds";

    // wire 21-28
    public float CumulusCloudTiling { get; init; } = 0.3f;
    public float CumulusCloudScrollU { get; init; }
    public float CumulusCloudScrollV { get; init; }
    public float CumulusCloudHeight { get; init; } = 1000f;
    public float StratusCloudTiling { get; init; } = 0.2f;
    public float StratusCloudScrollU { get; init; }
    public float StratusCloudScrollV { get; init; } = 0.002f;
    public float StratusCloudHeight { get; init; } = 8000f;

    // wire 29-32
    public float CloudAnimationSpeed { get; init; } = 0.09f;
    public float SkyClarity { get; init; } = 0.25f;
    public float CloudSilverLiningThickness { get; init; } = 7f;
    public float CloudSilverLiningBrightness { get; init; }

    /// <summary>
    /// The owner's fixed August KOTK sky: 14:00 is supplied separately by GameTimeSync; the four
    /// cloud weights and precipitation stay zero, while the proven non-zero fog baseline and the
    /// Z1-selected sun/wind/cloud-layer parameters preserve the intended colour and lighting.
    /// </summary>
    public static WeatherSettings Kotk2017 { get; } = new();

    /// <summary>Compatibility name for the fixed KOTK preset.</summary>
    public static WeatherSettings ClearDay => Kotk2017;

    public void WriteTo(PacketWriter writer)
    {
        if (!float.IsFinite(TransitionTime) || TransitionTime <= 0
            || !float.IsFinite(FogDensity) || FogDensity <= 0
            || !float.IsFinite(FogFloor) || FogFloor <= 0
            || !float.IsFinite(FogGradient) || FogGradient <= 0)
        {
            throw new InvalidOperationException(
                "Weather transition time and fog density/floor/gradient must be finite and positive.");
        }

        writer.WriteSingle(TransitionTime);
        writer.WriteSingle(FogDensity);
        writer.WriteSingle(FogFloor);
        writer.WriteSingle(FogGradient);
        writer.WriteSingle(GlobalPrecipitation);
        writer.WriteSingle(Temperature);
        writer.WriteSingle(Overcast);
        writer.WriteSingle(CloudWeight0);
        writer.WriteSingle(CloudWeight1);
        writer.WriteSingle(CloudWeight2);
        writer.WriteSingle(CloudWeight3);
        writer.WriteSingle(CloudShadows);
        writer.WriteSingle(SunAxisX);
        writer.WriteSingle(SunAxisY);
        writer.WriteSingle(SunAxisZ);
        writer.WriteSingle(WindDirX);
        writer.WriteSingle(WindDirY);
        writer.WriteSingle(WindDirZ);
        writer.WriteSingle(WindSpeed);
        writer.WriteSingle(RainMinStrength);
        writer.WriteSingle(RainRampUpTimeSeconds);
        writer.WriteString(CloudDensityTexture);
        writer.WriteSingle(CumulusCloudTiling);
        writer.WriteSingle(CumulusCloudScrollU);
        writer.WriteSingle(CumulusCloudScrollV);
        writer.WriteSingle(CumulusCloudHeight);
        writer.WriteSingle(StratusCloudTiling);
        writer.WriteSingle(StratusCloudScrollU);
        writer.WriteSingle(StratusCloudScrollV);
        writer.WriteSingle(StratusCloudHeight);
        writer.WriteSingle(CloudAnimationSpeed);
        writer.WriteSingle(SkyClarity);
        writer.WriteSingle(CloudSilverLiningThickness);
        writer.WriteSingle(CloudSilverLiningBrightness);
    }
}
