using System;

namespace ConfigGenerator;

/// <inheritdoc/>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ConstantConfigElement : ConfigElement
{
    /// <summary>
    /// Use a custom initializer
    /// <remarks>Overwrites the default env- or vault-loader</remarks>
    /// </summary>
    public string? Init { get; set; }
}