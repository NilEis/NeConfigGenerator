using System;

namespace ConfigGenerator;

/// <summary>
/// Defines a new property in the config class
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ConfigElement : Attribute
{
    /// <summary>
    /// The type of the property
    /// </summary>
    public Type Type { get; set; } = null!;

    /// <summary>
    /// The name of the property
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Use a custom initializer
    /// <remarks>Overwrites the default env- or vault-loader</remarks>
    /// </summary>
    public string? Init { get; set; }

    /// <summary>
    /// The default value if no value could be found
    /// </summary>
    public object? Default { get; set; }

    /// <summary>
    /// Replace content with '*' in logging
    /// </summary>
    public bool Anonymise { get; set; } = false;

    /// <summary>
    /// Output the variable in the start function
    /// </summary>
    public bool PrintVar { get; set; } = true;

    /// <summary>
    /// Convert the loaded value to <see cref="Type"/> using a JSON deserializer
    /// </summary>
    public bool IsJson { get; set; } = true;

    /// <summary>
    /// Load the value from this Path in the given openbao instance
    /// </summary>
    public string? FromVaultPath { get; set; }
}