using Casazen.Core.Models;
using Xunit;

namespace Casazen.Tests.Unit.Marketing;

/// <summary>
/// ADR-014-R5: Field catalog MUST exclude Alloggiati fields (document type, number, issue place)
/// and biometric/document data per compliance source veto.
/// </summary>
public class ConditionFieldCatalogTests
{
    [Fact]
    public void ConditionFields_NoDocumentOrAlloggiati_ProvesDataExclusionRule()
    {
        var forbiddenFields = new[]
        {
            "documentType",
            "documentNumber",
            "documentIssuePlace",
            "documentIssueDate",
            "documentExpiryDate",
            "alloggiatiDocumentType",
            "alloggiatiDocumentNumber",
            "alloggiatiIssuePlace",
            "alloggiatiSubmissionId",
            "alloggiatiStatus",

            "documentImage",
            "documentImageUrl",
            "documentRawData",
            "biometricData",
            "biometricVerificationId",
            "biometricHash",
            "faceImage",
            "fingerprintData",

            "mrzData",
            "ocrRawText",
            "documentMetadata"
        };

        var catalogFieldIds = FieldCatalog.Fields.Select(f => f.FieldId).ToHashSet();

        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.DoesNotContain(forbiddenField, catalogFieldIds);
        }

        Assert.NotEmpty(catalogFieldIds);
    }

    [Fact]
    public void ConditionFields_AllFieldsHaveValidOperators_EnsuresConsistency()
    {
        var fields = FieldCatalog.Fields;

        foreach (var field in fields)
        {
            Assert.NotNull(field.FieldId);
            Assert.NotEmpty(field.FieldId);
            Assert.NotNull(field.Operators);
            Assert.NotEmpty(field.Operators);
            Assert.NotNull(field.DisplayNameKey);
            Assert.NotEmpty(field.DisplayNameKey);
        }
    }

    [Fact]
    public void ConditionFields_GetField_ReturnsCorrectDefinition()
    {
        var expectedField = FieldCatalog.Fields[0];

        var field = FieldCatalog.GetField(expectedField.FieldId);

        Assert.NotNull(field);
        Assert.Equal(expectedField.FieldId, field.FieldId);
        Assert.Equal(expectedField.Type, field.Type);
    }

    [Fact]
    public void ConditionFields_IsValidField_ReturnsTrueForExistingField()
    {
        var fieldId = FieldCatalog.Fields[0].FieldId;

        var isValid = FieldCatalog.IsValidField(fieldId);

        Assert.True(isValid);
    }

    [Fact]
    public void ConditionFields_IsValidField_ReturnsFalseForForbiddenField()
    {
        var fieldId = "documentNumber";

        var isValid = FieldCatalog.IsValidField(fieldId);

        Assert.False(isValid);
    }

    [Fact]
    public void ConditionFields_IsValidOperator_ValidatesCorrectly()
    {
        var field = FieldCatalog.Fields.First(f => f.Type == ConditionFieldType.Number);
        var validOperator = field.Operators[0];
        var invalidOperator = "invalidOperator";

        Assert.True(FieldCatalog.IsValidOperator(field.FieldId, validOperator));
        Assert.False(FieldCatalog.IsValidOperator(field.FieldId, invalidOperator));
    }
}
