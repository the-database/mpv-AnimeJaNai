using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public static class JsonRpc
{
    public static void Validate(JsonObject message) => Contract.Require(message["jsonrpc"] is JsonValue version &&
        version.TryGetValue<string>(out var text) && text == "2.0",
        "invalid_message", "Expected JSON-RPC 2.0.");

    public static JsonNode RequestId(JsonObject message)
    {
        var id = message["id"];
        Contract.Require(id is JsonValue value && ((value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 80) ||
            (value.TryGetValue<long>(out long number) && number > 0 && number <= 9007199254740991)),
            "invalid_message", "Expected a bounded string or positive integer request id.");
        return id;
    }

    public static JsonObject Request(JsonNode id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["method"] = method, ["params"] = parameters,
    };
    public static JsonObject Result(JsonNode id, JsonNode? value) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = value?.DeepClone(),
    };
    public static JsonObject Error(JsonNode id, string code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code switch { "unknown_method" => -32601, "invalid_request" or "invalid_key" => -32602, _ => -32000 },
            ["message"] = message, ["data"] = new JsonObject { ["code"] = code },
        },
    };
}
