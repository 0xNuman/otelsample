using Microsoft.AspNetCore.Mvc;
using OtelSample.Application.DTOs;
using OtelSample.Application.Interfaces;

namespace OtelSample.Api.Controllers;

/// <summary>
/// Thin HTTP adapter for the orders domain. Contains no business logic and no direct
/// infrastructure access — it delegates everything to <see cref="IOrderFacade"/>.
///
/// Observability responsibility: none.
///   • The HTTP span (method, path, status code, duration) is created automatically
///     by OpenTelemetry.Instrumentation.AspNetCore.
///   • The <c>X-Tenant-Id</c> tag is set by <c>TenantResolutionMiddleware</c>.
///   • All business-level spans, metrics, and logs live in the Facade layer.
///
/// WHY keep the controller this thin:
///   If observability wiring lived in the controller, every new endpoint would need
///   manual span/metric boilerplate. The Facade centralises it once.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IOrderFacade facade) : ControllerBase
{
    /// <summary>
    /// Retrieves a single order by ID for the current tenant.
    /// Returns 404 if the order does not exist or belongs to a different tenant.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(int id, CancellationToken ct)
    {
        var result = await facade.GetOrderAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Creates a new order for the current tenant.
    /// Returns 201 Created with a Location header pointing to the new resource.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        var id = await facade.CreateOrderAsync(request, ct);
        return CreatedAtAction(nameof(GetOrder), new { id }, null);
    }

    /// <summary>
    /// Diagnostic endpoint: simulates an unhandled exception to verify the
    /// <c>GlobalExceptionHandler</c> returns a <c>correlationId</c> in the response body.
    /// Remove or protect this endpoint before deploying to production.
    /// </summary>
    [HttpGet("error-test")]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult ThrowError() =>
        throw new InvalidOperationException(
            "Intentional test exception — verify correlationId in response body.");
}
