using McpIt;
using Microsoft.AspNetCore.Mvc;

namespace SampleApi.Controllers;

// A perfectly ordinary ASP.NET Core controller. The only McpIt-specific thing here is
// the [McpTool] / [McpToolOutput] attributes: at BUILD TIME the McpIt source generator
// reads them and emits one MCP tool class per annotated action. There is no hand-written
// MCP server code. At run time each generated tool just loops back to the matching HTTP
// endpoint on this same app, so the REST API and the MCP tools never drift apart.
[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    // A tiny in-memory "database" so the demo is self-contained and runnable with no setup.
    private static readonly Order[] Orders =
    [
        new(1, "Ada Lovelace",  "shipped",   129.90m, "1Z-AAA-111", "Difference Engine sticker pack"),
        new(2, "Alan Turing",   "packing",    59.00m, null,         "Enigma-themed mug"),
        new(3, "Grace Hopper",  "delivered", 240.50m, "1Z-CCC-333", "Nanosecond wire (1ft)"),
    ];

    /// <summary>Lists all orders with a short summary line for each.</summary>
    // [McpTool] turns this action into an MCP tool. Because it is an HttpGet, McpIt derives
    // the safety hints automatically from the verb: ReadOnly = true, Destructive = false,
    // Idempotent = true. No name is given, so the tool name is the camelCased method name:
    // "listOrders". The <summary> above becomes the tool's description the model sees.
    [HttpGet]
    [McpTool]
    public IEnumerable<string> ListOrders() =>
        Orders.Select(o => $"#{o.Id} {o.Customer} ({o.Status})");

    /// <summary>Gets the full detail of a single order by its id.</summary>
    /// <param name="id">The numeric order id to look up.</param>
    // PER-PARAMETER DESCRIPTIONS. The XML <param> tag above is emitted as a [Description]
    // attribute on the generated tool's "id" input parameter and surfaced in the MCP
    // inputSchema, so agents see it alongside the type and required/optional flag.
    // TITLE. [McpTool(Title = "...")] sets the human-readable display name MCP clients may
    // show in their UI. Without Title, McpIt derives one from the method name in title case.
    [HttpGet("{id}")]
    [McpTool(Name = "getOrder", Title = "Get Order by ID")]
    public ActionResult<Order> GetOrderDetail(int id)
    {
        var order = Orders.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound() : order;
    }

    /// <summary>Returns just the shipping status and tracking number for an order.</summary>
    // OUTPUT SHAPING. The endpoint returns the whole Order object, but agents rarely need the
    // full payload. [McpToolOutput(Fields = ...)] tells the generated tool to keep ONLY these
    // top-level JSON properties before handing the result to the model, and MaxLength caps the
    // response size. This keeps tool responses small and cheap on tokens without changing the
    // underlying REST API (a browser hitting /orders/1/tracking still gets the full object).
    [HttpGet("{id}/tracking")]
    [McpTool(Name = "getOrderTracking")]
    [McpToolOutput(Fields = ["id", "status", "trackingNumber"], MaxLength = 400)]
    public ActionResult<Order> GetOrderTracking(int id)
    {
        var order = Orders.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound() : order;
    }

    /// <summary>Adds a note to an order and returns the updated order.</summary>
    // A tool WITH a request body. The [FromBody] parameter is serialized by the generated
    // tool via System.Text.Json. POST is a destructive verb, so AllowDestructive=true is
    // required to acknowledge it (otherwise MCPGEN002 fires at build time).
    [HttpPost("{id}/notes")]
    [McpTool(Name = "addOrderNote", AllowDestructive = true)]
    public ActionResult<Order> AddOrderNote(int id, [FromBody] AddNoteRequest request)
    {
        var order = Orders.FirstOrDefault(o => o.Id == id);
        if (order is null)
            return NotFound();
        // The demo "database" is read-only; echo the order back with the note appended to the item.
        return order with { Item = $"{order.Item} (note: {request.Note})" };
    }

    /// <summary>Returns projected line-item SKUs and the customer name for an order.</summary>
    /// <param name="id">The numeric order id to look up.</param>
    /// <param name="maxLines">Maximum number of line items to include, 1-50. Omit for the default of 10.</param>
    // NESTED/ARRAY PROJECTION. Fields accepts dot paths ("customer.name" drills into a nested
    // object) and array markers ("lines[].sku" projects each element of the array down to that
    // sub-property). MaxItems caps the number of array elements before MaxLength truncation.
    // Shaping order: project fields, cap items, truncate length.
    //
    // This action also demonstrates all three 1.4.0 annotation features together:
    //   1. Nested/array Fields + MaxItems on [McpToolOutput]
    //   2. Per-parameter descriptions via XML <param> tags (see above)
    //   3. Title on [McpTool]
    [HttpGet("{id}/lines")]
    [McpTool(Name = "getOrderLines", Title = "Order Lines")]
    [McpToolOutput(Fields = new[] { "id", "customer.name", "lines[].sku" }, MaxItems = 20, MaxLength = 2000)]
    public ActionResult<OrderDetail> GetOrderLines(int id, [FromQuery] int? maxLines = null)
    {
        var order = Orders.FirstOrDefault(o => o.Id == id);
        if (order is null)
            return NotFound();

        // Build a richer OrderDetail with nested customer info and line items.
        var count = Math.Clamp(maxLines ?? 10, 1, 50);
        var customer = new CustomerInfo(
            order.Customer,
            $"{order.Customer.ToLower().Replace(" ", ".")}@example.com");
        var lines = Enumerable.Range(1, count)
            .Select(i => new OrderLineItem($"SKU-{order.Id:D3}-{i:D2}", i, Math.Round(order.Total / count, 2)))
            .ToArray();
        return new OrderDetail(order.Id, customer, order.Status, lines);
    }
}

// Property names serialize as camelCase by default in ASP.NET Core, which is why the
// [McpToolOutput] Fields above use "trackingNumber" (matching the JSON, not the C# name).
public record Order(
    int Id,
    string Customer,
    string Status,
    decimal Total,
    string? TrackingNumber,
    string Item);

// The request body for addOrderNote. Its shape becomes the MCP tool's body input.
public record AddNoteRequest(string Note);

// Richer types used by getOrderLines to demonstrate nested-field and array projection.
// customer.name drills into CustomerInfo; lines[].sku projects each OrderLineItem element.
public record CustomerInfo(string Name, string Email);
public record OrderLineItem(string Sku, int Qty, decimal UnitPrice);
public record OrderDetail(int Id, CustomerInfo Customer, string Status, OrderLineItem[] Lines);
