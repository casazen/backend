using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PresetAgentTraining")]
public sealed class PresetAgentTraining
{
    [Key, MaxLength(40)]
    public string Key { get; set; } = "";

    [ConcurrencyCheck]
    public int Revision { get; set; }

    [Column(TypeName = "jsonb")]
    public string StateJson { get; set; } = "{}";
}
