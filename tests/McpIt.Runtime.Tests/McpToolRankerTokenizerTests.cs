using McpIt;

namespace McpIt.Runtime.Tests;

public class McpToolRankerTokenizerTests
{
    [Theory]
    [InlineData("getOrderById", "get order id")]
    [InlineData("GetOrderById", "get order id")]
    [InlineData("get_order_by_id", "get order id")]
    [InlineData("get-order-by-id", "get order id")]
    [InlineData("/api/orders/{id}", "api order id")]
    [InlineData("/api/v2/orders/{orderId:int}/items", "api v2 order order id int item")]
    [InlineData("HTTPServerStatus", "http server status")]
    [InlineData("loadJSON", "load json")]
    [InlineData("the list of my orders", "list order")]
    [InlineData("a b c", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Splits_cases_routes_and_drops_stopwords(string? input, string expected)
    {
        Assert.Equal(expected, string.Join(" ", McpToolRanker.Tokenize(input)));
    }

    [Theory]
    [InlineData("orders", "order")]
    [InlineData("ordered", "order")]
    [InlineData("ordering", "order")]
    [InlineData("categories", "category")]
    [InlineData("boxes", "box")]
    [InlineData("matches", "match")]
    [InlineData("addresses", "address")]
    [InlineData("status", "status")]
    [InlineData("shipped", "ship")]
    [InlineData("shipping", "ship")]
    [InlineData("ships", "ship")]
    [InlineData("billing", "bill")]
    [InlineData("processed", "process")]
    [InlineData("used", "used")]
    public void Folds_plurals_and_verb_endings(string word, string expected)
    {
        Assert.Equal(expected, McpToolRanker.Tokenize(word).Single());
    }

    [Theory]
    [InlineData("create", "creates", "created", "creating")]
    [InlineData("delete", "deletes", "deleted", "deleting")]
    [InlineData("invoice", "invoices", "invoiced", "invoicing")]
    [InlineData("update", "updates", "updated", "updating")]
    public void Inflections_of_one_word_fold_to_the_same_term(params string[] forms)
    {
        var stems = forms.Select(f => McpToolRanker.Tokenize(f).Single()).Distinct().ToList();
        Assert.Single(stems);
    }
}
