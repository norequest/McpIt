using McpIt;

namespace McpIt.Runtime.Tests;

// Ranking quality and determinism for McpToolRanker / McpToolIndex. Typos and true synonyms
// (without declared Keywords) are deliberately out of scope: matching is lexical.
public class McpToolRankerTests
{
    internal static McpToolDescriptor Tool(
        string name,
        string method,
        string route,
        string? description = null,
        string title = "",
        string? category = null,
        string[]? keywords = null,
        int priority = 0,
        string[]? parameters = null)
    {
        var readOnly = method is "GET" or "HEAD";
        return new McpToolDescriptor(
            name, title, description, method, route, category,
            keywords ?? [], priority, parameters ?? [],
            ReadOnly: readOnly, Destructive: !readOnly);
    }

    // A realistic 15-tool commerce API: orders, customers, invoices.
    internal static readonly IReadOnlyList<McpToolDescriptor> Catalog =
    [
        Tool("listOrders", "GET", "/api/orders", "Lists orders, newest first, optionally filtered by status.",
            "List Orders", "orders", parameters: ["status", "page"]),
        Tool("getOrderById", "GET", "/api/orders/{id}", "Gets a single order with its line items.",
            "Get Order by ID", "orders", parameters: ["id"]),
        Tool("createOrder", "POST", "/api/orders", "Places a new order for a customer.",
            "Create Order", "orders", keywords: ["purchase", "checkout", "buy"], parameters: ["customerId", "items"]),
        Tool("updateOrder", "PUT", "/api/orders/{id}", "Updates the shipping address or items of an order.",
            "Update Order", "orders", parameters: ["id", "order"]),
        Tool("cancelOrder", "POST", "/api/orders/{id}/cancel", "Cancels an order that has not shipped yet.",
            "Cancel Order", "orders", parameters: ["id", "reason"]),
        Tool("deleteOrder", "DELETE", "/api/orders/{id}", "Permanently deletes an order record.",
            "Delete Order", "orders", parameters: ["id"]),
        Tool("getOrderTracking", "GET", "/api/orders/{id}/tracking", "Returns the carrier and tracking number for a shipped order.",
            "Track Order", "orders", keywords: ["shipment", "delivery", "where"], parameters: ["id"]),
        Tool("listCustomers", "GET", "/api/customers", "Lists customers with paging.",
            "List Customers", "customers", parameters: ["page", "pageSize"]),
        Tool("getCustomer", "GET", "/api/customers/{id}", "Gets a customer profile by id.",
            "Get Customer", "customers", parameters: ["id"]),
        Tool("createCustomer", "POST", "/api/customers", "Registers a new customer account.",
            "Create Customer", "customers", keywords: ["signup"], parameters: ["customer"]),
        Tool("getOrderCustomer", "GET", "/api/orders/{id}/customer", "Returns the customer who placed an order.",
            "Order Buyer", "customers", keywords: ["buyer", "bought", "purchaser"], parameters: ["id"]),
        Tool("listInvoices", "GET", "/api/invoices", "Lists invoices, filterable by paid or unpaid.",
            "List Invoices", "billing", parameters: ["status"]),
        Tool("getInvoice", "GET", "/api/invoices/{id}", "Gets one invoice as JSON.",
            "Get Invoice", "billing", parameters: ["id"]),
        Tool("refundInvoice", "POST", "/api/invoices/{id}/refund", "Issues a full or partial refund against a paid invoice.",
            "Refund Invoice", "billing", keywords: ["money back", "chargeback"], parameters: ["id", "amount"]),
        Tool("sendInvoiceEmail", "POST", "/api/invoices/{id}/email", "Emails the invoice PDF to the customer.",
            "Email Invoice", "billing", keywords: ["mail", "resend"], parameters: ["id"]),
    ];

    private static string First(string query) => McpToolRanker.Rank(Catalog, query)[0].Tool.Name;

    [Theory]
    [InlineData("cancel my order", "cancelOrder")]
    [InlineData("who bought this", "getOrderCustomer")]
    [InlineData("refund invoice", "refundInvoice")]
    [InlineData("list customers", "listCustomers")]
    [InlineData("list all customers", "listCustomers")]
    [InlineData("show me the invoices", "listInvoices")]
    [InlineData("where is my package delivery", "getOrderTracking")]
    [InlineData("tracking number for an order", "getOrderTracking")]
    [InlineData("buy something", "createOrder")]
    [InlineData("checkout", "createOrder")]
    [InlineData("email the invoice to the customer", "sendInvoiceEmail")]
    [InlineData("sign up a new customer", "createCustomer")]
    [InlineData("give the customer their money back", "refundInvoice")]
    public void Realistic_queries_rank_the_expected_tool_first(string query, string expected)
    {
        Assert.Equal(expected, First(query));
    }

    [Theory]
    [InlineData("delete order", "deleteOrder")]
    [InlineData("remove an order", "deleteOrder")]
    [InlineData("change an order", "updateOrder")]
    [InlineData("edit order", "updateOrder")]
    [InlineData("new order", "createOrder")]
    [InlineData("get order", "getOrderById")]
    public void Verb_intent_picks_the_tool_whose_http_verb_fits(string query, string expected)
    {
        Assert.Equal(expected, First(query));
    }

    [Theory]
    [InlineData("getOrderById")]
    [InlineData("getorderbyid")]
    [InlineData("get_order_by_id")]
    [InlineData("get order by id")]
    public void Exact_tool_name_in_any_casing_wins(string query)
    {
        var results = McpToolRanker.Rank(Catalog, query);
        Assert.Equal("getOrderById", results[0].Tool.Name);
        Assert.True(results.Count == 1 || results[0].Score > results[1].Score * 1.5, "exact name should win clearly");
    }

    [Fact]
    public void Name_prefix_query_finds_the_tool()
    {
        Assert.Equal("getOrderTracking", First("getOrderTrack"));
    }

    [Fact]
    public void Query_word_matches_longer_indexed_words_by_prefix()
    {
        // "cancel" is not a stem of "cancellation", but it is a prefix of it.
        var tools = new[]
        {
            Tool("requestCancellation", "POST", "/subscriptions/{id}/cancellation", "Starts a subscription cancellation."),
            Tool("getSubscription", "GET", "/subscriptions/{id}", "Gets a subscription."),
        };

        var results = McpToolRanker.Rank(tools, "cancel subscription");
        Assert.Equal("requestCancellation", results[0].Tool.Name);

        // A truncated word still finds its tools.
        var invo = McpToolRanker.Rank(Catalog, "invo", top: 25);
        Assert.NotEmpty(invo);
        Assert.All(invo, r => Assert.Contains("Invoice", r.Tool.Name));
    }

    [Fact]
    public void Exact_term_match_outranks_prefix_match()
    {
        // "ship" is an exact term of getShip but only a prefix of "shipment" in getShipmentStatus:
        // both match, the exact one ranks first.
        var tools = new[]
        {
            Tool("getShipmentStatus", "GET", "/shipments/{id}/status"),
            Tool("getShip", "GET", "/ships/{id}"),
        };

        var results = McpToolRanker.Rank(tools, "ship");
        Assert.Equal(["getShip", "getShipmentStatus"], results.Select(r => r.Tool.Name));
    }

    [Fact]
    public void Name_match_outranks_description_only_match()
    {
        var tools = new[]
        {
            Tool("archiveReport", "POST", "/reports/{id}/archive", "Archives a report so it is hidden from the shipment dashboard."),
            Tool("listShipments", "GET", "/shipments", "Lists everything."),
        };

        Assert.Equal("listShipments", McpToolRanker.Rank(tools, "shipments")[0].Tool.Name);
    }

    [Fact]
    public void Keywords_outrank_description()
    {
        var tools = new[]
        {
            Tool("alpha", "GET", "/a", "Mentions widget once in a long description of an unrelated operation."),
            Tool("beta", "GET", "/b", "Does something.", keywords: ["widget"]),
        };

        Assert.Equal("beta", McpToolRanker.Rank(tools, "widget")[0].Tool.Name);
    }

    [Fact]
    public void Priority_breaks_ties_between_identical_matches()
    {
        var tools = new[]
        {
            Tool("searchA", "GET", "/a", "Search products."),
            Tool("searchB", "GET", "/b", "Search products.", priority: 3),
        };

        var results = McpToolRanker.Rank(tools, "products");
        Assert.Equal("searchB", results[0].Tool.Name);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void Priority_does_not_override_a_clearly_better_match()
    {
        var tools = new[]
        {
            Tool("getWeather", "GET", "/weather", "Current weather."),
            Tool("listCities", "GET", "/cities", "Lists cities with their weather station.", priority: 10),
        };

        Assert.Equal("getWeather", McpToolRanker.Rank(tools, "weather")[0].Tool.Name);
    }

    [Fact]
    public void Non_matching_tools_are_excluded()
    {
        var results = McpToolRanker.Rank(Catalog, "invoice", top: 25);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Score > 0));
        Assert.DoesNotContain(results, r => r.Tool.Name == "listCustomers");
    }

    [Fact]
    public void Unknown_words_return_no_results()
    {
        Assert.Empty(McpToolRanker.Rank(Catalog, "xylophone quantum"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the of and")]
    public void Empty_query_returns_tools_by_priority_then_name(string query)
    {
        var tools = new[]
        {
            Tool("zeta", "GET", "/z"),
            Tool("alpha", "GET", "/a"),
            Tool("mid", "GET", "/m", priority: 5),
            Tool("low", "GET", "/l", priority: -1),
        };

        var results = McpToolRanker.Rank(tools, query, top: 3);

        Assert.Equal(["mid", "alpha", "zeta"], results.Select(r => r.Tool.Name));
        Assert.All(results, r => Assert.Equal(0, r.Score));
    }

    [Fact]
    public void Top_limits_the_result_count()
    {
        Assert.Equal(2, McpToolRanker.Rank(Catalog, "order", top: 2).Count);
    }

    [Fact]
    public void Top_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => McpToolRanker.Rank(Catalog, "order", top: 0));
    }

    [Fact]
    public void Ordering_is_deterministic_and_input_order_independent()
    {
        var reversed = Catalog.Reverse().ToList();

        foreach (var query in new[] { "order", "customer", "invoice", "get", "list", "id" })
        {
            var a = McpToolRanker.Rank(Catalog, query, top: 25).Select(r => (r.Tool.Name, r.Score)).ToList();
            var b = McpToolRanker.Rank(reversed, query, top: 25).Select(r => (r.Tool.Name, r.Score)).ToList();
            Assert.Equal(a.Select(x => x.Name), b.Select(x => x.Name));
            Assert.Equal(a.Select(x => x.Score), b.Select(x => x.Score));

            // Sorted by score desc, then priority desc, then name ordinal.
            for (var i = 1; i < a.Count; i++)
            {
                Assert.True(a[i - 1].Score > a[i].Score ||
                    (a[i - 1].Score == a[i].Score && string.CompareOrdinal(a[i - 1].Name, a[i].Name) < 0));
            }
        }
    }

    [Fact]
    public void Empty_catalog_returns_nothing()
    {
        Assert.Empty(McpToolRanker.Rank([], "order"));
        Assert.Empty(McpToolRanker.Rank([], ""));
    }

    [Fact]
    public void Single_tool_catalog_still_matches()
    {
        // IDF must stay positive even when a term occurs in every tool.
        var results = McpToolRanker.Rank([Tool("listOrders", "GET", "/orders")], "orders");
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public void Null_keyword_and_parameter_lists_are_tolerated()
    {
        var tool = new McpToolDescriptor("pingServer", "", null, "GET", "/ping", null, null!, 0, null!, true, false);
        Assert.Equal("pingServer", McpToolRanker.Rank([tool], "ping")[0].Tool.Name);
    }

    [Fact]
    public void Index_handles_a_thousand_tools_quickly()
    {
        var verbs = new[] { ("get", "GET"), ("list", "GET"), ("create", "POST"), ("update", "PUT"), ("delete", "DELETE") };
        var tools = new List<McpToolDescriptor>();
        for (var i = 0; i < 200; i++)
        {
            foreach (var (verb, method) in verbs)
            {
                tools.Add(Tool($"{verb}Entity{i}", method, $"/api/entity{i}/{{id}}",
                    $"{verb} an entity number {i} in resource group {i % 17}.", category: $"group{i % 17}",
                    parameters: ["id", "filter"]));
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = new McpToolIndex(tools);
        for (var q = 0; q < 200; q++)
            index.Search($"delete entity{q} group", 5);
        sw.Stop();

        Assert.Equal(1000, index.Count);
        Assert.Equal("deleteEntity42", index.Search("delete entity42", 5)[0].Tool.Name);
        // Generous bound (build + 200 queries) so slow CI agents do not flake.
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
