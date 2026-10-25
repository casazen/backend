namespace Casazen.Core.Models;

/// <summary>
/// Field type for condition builder (ADR-014-R2).
/// </summary>
public enum ConditionFieldType
{
    Text,
    Number,
    Date,
    Boolean,
    Enum,
    Set
}

/// <summary>
/// Field definition: ID, type, and available operators.
/// </summary>
public record FieldDefinition(
    string FieldId,
    ConditionFieldType Type,
    string DisplayNameKey,
    string[] Operators);

/// <summary>
/// Typed field catalog for condition builder (ADR-014-R2, ADR-014-R5).
/// Excludes Alloggiati fields (document type, number, issue place) per compliance veto.
/// Excludes raw document images and biometric data per ADR-014-R5.
/// </summary>
public static class FieldCatalog
{
    /// <summary>All available fields for condition builder.</summary>
    public static readonly FieldDefinition[] Fields = new[]
    {
        // Routine fields - booking/property criteria
        new FieldDefinition(
            "propertyId",
            ConditionFieldType.Set,
            "field.propertyId",
            new[] { "in", "notIn" }),

        new FieldDefinition(
            "propertyTag",
            ConditionFieldType.Set,
            "field.propertyTag",
            new[] { "in", "notIn" }),

        new FieldDefinition(
            "channel",
            ConditionFieldType.Enum,
            "field.channel",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "stayDuration",
            ConditionFieldType.Number,
            "field.stayDuration",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        new FieldDefinition(
            "guestCount",
            ConditionFieldType.Number,
            "field.guestCount",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        new FieldDefinition(
            "bookingLeadTime",
            ConditionFieldType.Number,
            "field.bookingLeadTime",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        new FieldDefinition(
            "paymentStatus",
            ConditionFieldType.Enum,
            "field.paymentStatus",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "checkInStatus",
            ConditionFieldType.Enum,
            "field.checkInStatus",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "idVerificationStatus",
            ConditionFieldType.Enum,
            "field.idVerificationStatus",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "guestLanguage",
            ConditionFieldType.Enum,
            "field.guestLanguage",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "freeNights",
            ConditionFieldType.Number,
            "field.freeNights",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        // Segment fields - guest history/profile
        new FieldDefinition(
            "guestOrigin",
            ConditionFieldType.Enum,
            "field.guestOrigin",
            new[] { "equals", "notEquals", "in", "notIn" }),

        new FieldDefinition(
            "totalStays",
            ConditionFieldType.Number,
            "field.totalStays",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        new FieldDefinition(
            "country",
            ConditionFieldType.Enum,
            "field.country",
            new[] { "in", "notIn" }),

        new FieldDefinition(
            "language",
            ConditionFieldType.Enum,
            "field.language",
            new[] { "in", "notIn" }),

        new FieldDefinition(
            "totalSpend",
            ConditionFieldType.Number,
            "field.totalSpend",
            new[] { "equals", "greaterThan", "lessThan", "between" }),

        new FieldDefinition(
            "lastStayAt",
            ConditionFieldType.Date,
            "field.lastStayAt",
            new[] { "before", "after", "between", "inLast" }),

        new FieldDefinition(
            "firstStayAt",
            ConditionFieldType.Date,
            "field.firstStayAt",
            new[] { "before", "after", "between", "inLast" }),

        new FieldDefinition(
            "stayPeriod",
            ConditionFieldType.Date,
            "field.stayPeriod",
            new[] { "before", "after", "between", "inLast" }),

        new FieldDefinition(
            "propertyHistory",
            ConditionFieldType.Set,
            "field.propertyHistory",
            new[] { "in", "notIn" }),

        new FieldDefinition(
            "hasMarketingConsent",
            ConditionFieldType.Boolean,
            "field.hasMarketingConsent",
            new[] { "is" }),

        new FieldDefinition(
            "hasEmailConsent",
            ConditionFieldType.Boolean,
            "field.hasEmailConsent",
            new[] { "is" }),

        new FieldDefinition(
            "hasSmsConsent",
            ConditionFieldType.Boolean,
            "field.hasSmsConsent",
            new[] { "is" }),

        new FieldDefinition(
            "hasWhatsAppConsent",
            ConditionFieldType.Boolean,
            "field.hasWhatsAppConsent",
            new[] { "is" })
    };

    /// <summary>
    /// Get field definition by ID.
    /// </summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>Field definition, or null if not found.</returns>
    public static FieldDefinition? GetField(string fieldId)
    {
        return Fields.FirstOrDefault(f => f.FieldId == fieldId);
    }

    /// <summary>
    /// Check if a field ID is valid.
    /// </summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>True if field exists in catalog.</returns>
    public static bool IsValidField(string fieldId)
    {
        return Fields.Any(f => f.FieldId == fieldId);
    }

    /// <summary>
    /// Check if an operator is valid for a field.
    /// </summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="operator">Operator to validate.</param>
    /// <returns>True if operator is valid for the field.</returns>
    public static bool IsValidOperator(string fieldId, string @operator)
    {
        var field = GetField(fieldId);
        return field?.Operators.Contains(@operator) ?? false;
    }
}
