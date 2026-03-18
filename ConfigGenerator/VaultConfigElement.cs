using System;

namespace ConfigGenerator;

/// <inheritdoc/>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VaultConfigElement : ConfigElement
{
    /// <summary>
    /// Load the value from this Path in the given openbao instance
    /// </summary>
    public string? FromVaultPath { get; set; }
}