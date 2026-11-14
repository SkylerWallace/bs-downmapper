using System.Collections;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace System.Text.Json
{
    internal static class JsonSerializer
    {
        internal static Nodes.JsonNode SerializeToNode(object value) => Nodes.JsonNode.Wrap(JToken.FromObject(value));
        internal static string Serialize(object value) => JsonConvert.SerializeObject(value);
    }
}

namespace System.Text.Json.Nodes
{
    public abstract class JsonNode
    {
        internal JToken Token { get; }
        private protected JsonNode(JToken token) => Token = token;

        internal static JsonNode Wrap(JToken token) => token switch
        {
            JObject obj => new JsonObject(obj),
            JArray array => new JsonArray(array),
            JValue value when value.Type != JTokenType.Null => new JsonValue(value),
            _ => null,
        };

        internal static JsonNode Parse(string text)
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var result = JToken.ReadFrom(reader);
            if (reader.Read()) throw new JsonReaderException("Unexpected content after JSON.");
            return Wrap(result);
        }

        public JsonNode DeepClone() => Wrap(Token.DeepClone());
        public string ToJsonString() => Token.ToString(Formatting.None);
        public JsonObject AsObject() => this as JsonObject ?? throw new InvalidOperationException("Expected a JSON object.");
        public JsonArray AsArray() => this as JsonArray ?? throw new InvalidOperationException("Expected a JSON array.");
        public virtual JsonNode this[string key] { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
        public virtual JsonNode this[int index] { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
        public T GetValue<T>() => this is JsonValue scalar && scalar.TryGetValue<T>(out var value)
            ? value : throw new InvalidOperationException($"Expected a JSON {typeof(T).Name}.");

        public static implicit operator JsonNode(string value) => value == null ? null : new JsonValue(new JValue(value));
        public static implicit operator JsonNode(bool value) => new JsonValue(new JValue(value));
        public static implicit operator JsonNode(int value) => new JsonValue(new JValue(value));
        public static implicit operator JsonNode(long value) => new JsonValue(new JValue(value));
        public static implicit operator JsonNode(double value) => new JsonValue(new JValue(value));
        public static implicit operator JsonNode(float value) => new JsonValue(new JValue(value));
    }

    public sealed class JsonValue : JsonNode
    {
        internal JsonValue(JValue value) : base(value) { }
        public static JsonValue Create<T>(T value) => new JsonValue(new JValue(value));

        public bool TryGetValue<T>(out T result)
        {
            result = default;
            var type = typeof(T);
            bool number = Token.Type is JTokenType.Integer or JTokenType.Float;
            bool valid = type == typeof(string) ? Token.Type == JTokenType.String
                : type == typeof(bool) ? Token.Type == JTokenType.Boolean
                : type == typeof(int) || type == typeof(long) ? Token.Type == JTokenType.Integer
                : number && (type == typeof(double) || type == typeof(float) || type == typeof(decimal));
            if (!valid) return false;
            try
            {
                result = (T)Convert.ChangeType(((JValue)Token).Value, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (OverflowException) { return false; }
        }
    }

    public sealed class JsonObject : JsonNode, IEnumerable<KeyValuePair<string, JsonNode>>
    {
        private JObject Object => (JObject)Token;
        internal JsonObject(JObject value) : base(value) { }
        public JsonObject() : this(new JObject()) { }
        public override JsonNode this[string key] { get => Wrap(Object[key]); set => Object[key] = value?.Token; }
        public int Count => Object.Count;
        public void Add(string key, JsonNode value) => Object.Add(key, value?.Token);
        public bool ContainsKey(string key) => Object.Property(key) != null;
        public bool Remove(string key) => Object.Remove(key);
        public IEnumerator<KeyValuePair<string, JsonNode>> GetEnumerator() =>
            Object.Properties().Select(property => new KeyValuePair<string, JsonNode>(property.Name, Wrap(property.Value))).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class JsonArray : JsonNode, IEnumerable<JsonNode>
    {
        private JArray Array => (JArray)Token;
        internal JsonArray(JArray value) : base(value) { }
        public JsonArray(params JsonNode[] values) : this(new JArray())
        {
            foreach (var value in values) Add(value);
        }
        public override JsonNode this[int index] { get => Wrap(Array[index]); set => Array[index] = value?.Token; }
        public int Count => Array.Count;
        public int IndexOf(JsonNode value) => Array.IndexOf(value?.Token);
        public void Add(JsonNode value) => Array.Add(value?.Token ?? JValue.CreateNull());
        public IEnumerator<JsonNode> GetEnumerator() => Array.Select(Wrap).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
