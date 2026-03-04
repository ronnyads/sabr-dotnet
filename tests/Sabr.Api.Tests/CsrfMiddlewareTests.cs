using Microsoft.AspNetCore.Http;
using Sabr.Api.Security;

namespace Sabr.Api.Tests;

public sealed class CsrfMiddlewareTests
{
    [Fact]
    public async Task TenantRefresh_MissingTokens_Returns403()
    {
        var called = false;
        var middleware = new CsrfMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/auth/refresh";

        await middleware.InvokeAsync(ctx);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task TenantRefresh_WithMatchingTokens_CallsNext()
    {
        var called = false;
        var middleware = new CsrfMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var token = "abc123";
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/auth/refresh";
        ctx.Request.Headers["Cookie"] = $"{CsrfMiddleware.TenantCookieName}={token}";
        ctx.Request.Headers[CsrfMiddleware.TenantHeaderName] = token;

        await middleware.InvokeAsync(ctx);

        Assert.True(called);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }
}

