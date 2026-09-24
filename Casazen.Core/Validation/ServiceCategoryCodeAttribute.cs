using System.ComponentModel.DataAnnotations;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Validation;

/// <summary>
/// The value must be exactly one of <see cref="ServiceCategories.All"/> (model validation, 400
/// <c>validation_error</c>). Same check as the <see cref="AllowedValuesAttribute"/> it replaces, but the list comes
/// from the single source of truth instead of a second copy in the attribute (SU-03).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ServiceCategoryCodeAttribute() : AllowedValuesAttribute([.. ServiceCategories.All]);
