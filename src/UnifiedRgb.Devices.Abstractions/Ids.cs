namespace UnifiedRgb.Devices;

/// <summary>Stable controller identifier, e.g. "lianli:rf:AA-BB-CC-DD-EE-FF" or "virtual:fan0".</summary>
public readonly record struct ControllerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Zone index within one controller.</summary>
public readonly record struct ZoneId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
