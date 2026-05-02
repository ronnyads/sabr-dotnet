using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api;
using Sabr.Application.Validation;

namespace Sabr.Api.Tests;

/// <summary>
/// Testes unitários para ServiceResult&lt;T&gt; e ServiceResultExtensions.
/// Valida todos os factory helpers e o mapeamento ErrorCode → HTTP status code.
/// </summary>
public sealed class ServiceResultTests
{
    // ── ServiceResult factory helpers ─────────────────────────────────────────

    [Fact]
    public void Success_SetsSucceededAndData()
    {
        var result = ServiceResult<string>.Success("hello");

        Assert.True(result.Succeeded);
        Assert.Equal("hello", result.Data);
        Assert.Empty(result.Errors);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Failure_WithErrors_SetsErrors()
    {
        var result = ServiceResult<string>.Failure(new[]
        {
            new ValidationError("field1", "msg1"),
            new ValidationError("field2", "msg2")
        });

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Failure_WithErrorCode_SetsCodeAndErrors()
    {
        var result = ServiceResult<int>.Failure(
            ServiceErrorCodes.NotFound,
            new[] { new ValidationError("id", "not found") });

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCodes.NotFound, result.ErrorCode);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void NotFound_SetsNotFoundCode()
    {
        var result = ServiceResult<string>.NotFound("categoryId", "Category not found");

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCodes.NotFound, result.ErrorCode);
        Assert.Contains(result.Errors, e => e.Field == "categoryId");
    }

    [Fact]
    public void Conflict_SetsConflictCode()
    {
        var result = ServiceResult<string>.Conflict("version", "Concurrent edit");

        Assert.Equal(ServiceErrorCodes.ConcurrencyConflict, result.ErrorCode);
    }

    [Fact]
    public void Forbidden_SetsForbiddenCode()
    {
        var result = ServiceResult<bool>.Forbidden("sku", "SKU not authorized");

        Assert.Equal(ServiceErrorCodes.Forbidden, result.ErrorCode);
    }

    // ── ToActionResult — sucesso ──────────────────────────────────────────────

    [Fact]
    public void ToActionResult_Success_Returns200()
    {
        var result = ServiceResult<string>.Success("ok");
        var action = result.ToActionResult();

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal("ok", ok.Value);
    }

    [Fact]
    public void ToActionResult_Success_201_Returns201()
    {
        var result = ServiceResult<string>.Success("created");
        var action = result.ToActionResult(successStatus: StatusCodes.Status201Created);

        var obj = Assert.IsType<ObjectResult>(action);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public void ToActionResult_Success_204_ReturnsNoContent()
    {
        var result = ServiceResult<bool>.Success(true);
        var action = result.ToActionResult(successStatus: StatusCodes.Status204NoContent);

        Assert.IsType<NoContentResult>(action);
    }

    // ── ToActionResult — mapeamento de ErrorCode ──────────────────────────────

    [Theory]
    [InlineData(ServiceErrorCodes.NotFound,            404)]
    [InlineData(ServiceErrorCodes.Forbidden,           403)]
    [InlineData(ServiceErrorCodes.ConcurrencyConflict, 409)]
    [InlineData(ServiceErrorCodes.PreconditionRequired,428)]
    [InlineData(ServiceErrorCodes.SkuNotAuthorized,    403)]
    [InlineData(ServiceErrorCodes.DraftNotFound,       404)]
    [InlineData(ServiceErrorCodes.PricingInvalid,      422)]
    [InlineData(ServiceErrorCodes.CategoryNotFound,    422)]
    [InlineData(ServiceErrorCodes.CategoryInactive,    422)]
    [InlineData(ServiceErrorCodes.CategoryHasActiveChildren, 422)]
    [InlineData(ServiceErrorCodes.CategoryCycleDetected,     422)]
    [InlineData(ServiceErrorCodes.VariantAlreadyExists, 409)]
    [InlineData(ServiceErrorCodes.AnatelRequired,      422)]
    [InlineData(ServiceErrorCodes.MlAuthInvalid,       401)]
    public void ToActionResult_ErrorCode_MapsToCorrectHttpStatus(string errorCode, int expectedStatus)
    {
        var result = ServiceResult<string>.Failure(errorCode, "field", "message");
        var action = result.ToActionResult(traceId: "trace-abc");

        var obj = Assert.IsType<ObjectResult>(action);
        Assert.Equal(expectedStatus, obj.StatusCode);
    }

    [Fact]
    public void ToActionResult_ValidationFailure_WithoutCode_Returns422()
    {
        var result = ServiceResult<int>.Failure(new[]
        {
            new ValidationError("name", "Name is required")
        });
        var action = result.ToActionResult();

        var obj = Assert.IsType<ObjectResult>(action);
        Assert.Equal(422, obj.StatusCode);
    }

    [Fact]
    public void ToActionResult_TraceId_IsIncludedInBody()
    {
        var result = ServiceResult<string>.NotFound("x", "not found");
        var action = result.ToActionResult(traceId: "trace-xyz");

        var obj = Assert.IsType<ObjectResult>(action);
        var error = Assert.IsType<Sabr.Application.Models.ApiError>(obj.Value);
        Assert.Equal("trace-xyz", error.TraceId);
    }
}
