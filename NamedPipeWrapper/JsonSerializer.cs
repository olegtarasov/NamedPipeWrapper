using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;

namespace NamedPipeWrapper
{
    public static class JsonSerializer
    {
        private static readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All, TypeNameAssemblyFormat = FormatterAssemblyStyle.Full };

        public static string Serialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj, _serializerSettings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, _serializerSettings);
        }

        public static object Deserialize(string json)
        {
            return JsonConvert.DeserializeObject(json, _serializerSettings);
        }
    }
}