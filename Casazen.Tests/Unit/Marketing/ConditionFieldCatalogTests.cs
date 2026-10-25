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
        // Arrange: forbidden field IDs that would expose sensitive compliance data
        var forbiddenFields = new[]
        {
            // Alloggiati fields (ADR-014-R5)
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

            // Document/biometric fields (ADR-014-R5)
            "documentImage",
            "documentImageUrl",
            "documentRawData",
            "biometricData",
            "biometricVerificationId",
            "biometricHash",
            "faceImage",
            "fingerprintData",

            // Raw document extraction fields
            "mrzData",
            "ocrRawText",
            "documentMetadata"
        };

        // Act: check each forbidden field against the catalog
        var catalogFieldIds = FieldCatalog.Fields.Select(f => f.FieldId).ToHashSet();

        // Assert: none of the forbidden fields should exist in the catalog
        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.DoesNotContain(forbiddenField, catalogFieldIds);
        }

        // Additional assertion: validate catalog is not empty
        Assert.NotEmpty(catalogFieldIds);
    }

    [Fact]
    public void ConditionFields_AllFieldsHaveValidOperators_EnsuresConsistency()
    {
        // Arrange & Act: every field in the catalog
        var fields = FieldCatalog.Fields;

        // Assert: each field has at least one operator
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
        // Arrange
        var expectedField = FieldCatalog.Fields[0];

        // Act
        var field = FieldCatalog.GetField(expectedField.FieldId);

        // Assert
        Assert.NotNull(field);
        Assert.Equal(expectedField.FieldId, field.FieldId);
        Assert.Equal(expectedField.Type, field.Type);
    }

    [Fact]
    public void ConditionFields_IsValidField_ReturnsTrueForExistingField()
    {
        // Arrange
        var fieldId = FieldCatalog.Fields[0].FieldId;

        // Act
        var isValid = FieldCatalog.IsValidField(fieldId);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ConditionFields_IsValidField_ReturnsFalseForForbiddenField()
    {
        // Arrange: a forbidden field that must not exist
        var fieldId = "documentNumber";

        // Act
        var isValid = FieldCatalog.IsValidField(fieldId);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ConditionFields_IsValidOperator_ValidatesCorrectly()
    {
        // Arrange
        var field = FieldCatalog.Fields.First(f => f.Type == ConditionFieldType.Number);
        var validOperator = field.Operators[0];
        var invalidOperator = "invalidOperator";

        // Act & Assert
        Assert.True(FieldCatalog.IsValidOperator(field.FieldId, validOperator));
        Assert.False(FieldCatalog.IsValidOperator(field.FieldId, invalidOperator));
    }
}
