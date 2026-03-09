using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using System.Web;
using System.Reflection;
using System.IO.Compression;
using System.Text.Encodings.Web;


namespace common.utils;

public static class Utils
{

  public static Encoding Utf8EncodingNoBom { get; } = new UTF8Encoding(false);

  public static string GetExeDir(bool relative = false)
  {
    if (relative)
    {
      return Directory.GetCurrentDirectory();
    }
    return AppContext.BaseDirectory;
  }

  public static bool TryClear<Tval>(this IEnumerable<Tval> dest)
  {
    if (dest is null)
    {
      return false;
    }

    if (dest is ICollection<Tval> icdest && !icdest.IsReadOnly)
    {
      icdest.Clear();
      return true;
    }

    return false;
  }

  public static void CopyFrom<Tkey, Tval>(this IEnumerable<KeyValuePair<Tkey, Tval>> dest, IEnumerable<KeyValuePair<Tkey, Tval>> src)
  {
    if (dest is null ||  src is null)
    {
      return;
    }

    if (dest is IDictionary<Tkey, Tval> iddest && !iddest.IsReadOnly)
    {
      foreach (var item in src)
      {
        iddest[item.Key] = item.Value;
      }
    }
    else
    {
      throw new ArgumentException("Destination is not copyable");
    }
  }

  public static void CopyFrom<Tval>(this IEnumerable<Tval> dest, IEnumerable<Tval> src)
  {
    if (dest is null || src is null)
    {
      return;
    }

    if (dest is List<Tval> ldest)
    {
      ldest.AddRange(src);
    }
    else if (dest is ICollection<Tval> icdest && !icdest.IsReadOnly)
    {
      foreach (var item in src)
      {
        icdest.Add(item);
      }
    }
    else
    {
      throw new ArgumentException("Destination is not copyable");
    }
  }


  enum JsonContext
  {
    DUMMY_START, // for initial start
    OBJECT, // non-terminal
    ARRAY, // non-terminal
  }

  /// <summary>
  /// Allows duplicate keys
  /// </summary>
  public static JsonNode? ParseJsonLenient(string? str)
  {
    if (string.IsNullOrWhiteSpace(str))
    {
      return null;
    }

    static JsonNode? ParseJson(ref Utf8JsonReader reader)
    {
      // what are we doing currently
      Stack<JsonContext> contexts = new([JsonContext.DUMMY_START]);
      // what are still processing
      Stack<JsonNode?> values = new();

      JsonNode? ReadNextJsonValue(ref Utf8JsonReader reader)
      {
        switch (reader.TokenType)
        {
          case JsonTokenType.StartObject:
            {
              var newObj = new JsonObject();
              contexts.Push(JsonContext.OBJECT);
              // upcoming val should be on top, needs further processing
              values.Push(newObj);
              return newObj;
            }

          case JsonTokenType.StartArray:
            {
              var newArr = new JsonArray();
              contexts.Push(JsonContext.ARRAY);
              // upcoming val should be on top, needs further processing
              values.Push(newArr);
              return newArr;
            }

          case JsonTokenType.String:
          case JsonTokenType.Number:
          case JsonTokenType.Null:
          case JsonTokenType.True:
          case JsonTokenType.False:
            // we don't put this value on the stack since we won't process it any further
            return JsonNode.Parse(ref reader);

          default:
            throw new JsonException($"Expected JSON value, got '{reader.TokenType}'");
        }
      }

      while (true)
      {
        if (contexts.Count == 0)
        {
          break;
        }

        switch (contexts.Peek())
        {
          case JsonContext.DUMMY_START:
            contexts.Pop();
            if (reader.Read())
            {
              // for non-terminal nodes (obj, array) this line pushes the value twice
              // the reader function pushes it for context, and we push it as a final return value
              // the extra push is later poped by the relevant context handler case
              // and we end up with 1 value on the stack as expected
              values.Push(ReadNextJsonValue(ref reader));
            }
            else
            {
              values.Push(null);
            }
            break;

          case JsonContext.OBJECT:
            if (!reader.Read())
            {
              throw new JsonException("Unbalanced object notation");
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
              contexts.Pop(); // stop processing json dict
              values.Pop(); // this json dict is no longer needed
            }
            else
            {
              if (reader.TokenType != JsonTokenType.PropertyName)
              {
                throw new JsonException("Expected a key for JSON object");
              }
              // we must grab the active/current object first, because reading next value might push a new value for upcoming context
              var jnode = values.Peek() ?? throw new JsonException("JSON object is null");
              var currentObj = jnode.AsObject();
              var key = reader.GetString() ?? throw new JsonException("Object key is null");
              if (!reader.Read())
              {
                throw new JsonException("Expected a value for JSON object");
              }
              var newVal = ReadNextJsonValue(ref reader);
              // if we have that key already, conver the current dict to array
              if (
                currentObj.TryGetPropertyValue(key, out var oldVal)
                && oldVal?.GetValueKind() != JsonValueKind.Array
              )
              {
                // detach it from parent first otherwise it throws:
                // "'System.InvalidOperationException' occurred in System.Text.Json.dll 'The node already has a parent.'"
                currentObj.Remove(key);
                oldVal = new JsonArray(oldVal);
                currentObj[key] = oldVal;
              }

              if (oldVal?.GetValueKind() == JsonValueKind.Array)
              {
                oldVal.AsArray().Add(newVal);
              }
              else
              {
                currentObj[key] = newVal;
              }
            }
            break;

          case JsonContext.ARRAY:
            if (!reader.Read())
            {
              throw new JsonException("Unbalanced array notation");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
              contexts.Pop(); // stop processing json array
              values.Pop(); // this json array is no longer needed
            }
            else
            {
              // we must grab the active/current array first, because reading next value might push a new value for upcoming context
              var jnode = values.Peek() ?? throw new JsonException("JSON array is null");
              var thisArr = jnode.AsArray();
              thisArr.Add(ReadNextJsonValue(ref reader));
            }
            break;
        }
      }

      if (!reader.IsFinalBlock)
      {
        throw new JsonException("Unparsed JSON data");
      }
      if (values.Count > 1)
      {
        throw new JsonException("Too many top level JSON nodes");
      }
      if (values.Count == 0)
      {
        throw new JsonException("Failed to parse any JSON data");
      }

      // // test me
      // Console.WriteLine(Utils.ParseJsonLenient("""
      // {
      //   "array": [
      //     {
      //       "key 1": 1,
      //       "key 2": [
      //         2,
      //         3,
      //         {
      //           "inner key": {
      //             "most inner key": [
      //               "hi",
      //               "123",
      //               false,
      //               null,
      //               true,
      //               4,
      //             ],
      //           },
      //         },
      //         "my str",
      //       ],
      //       "key 1": "pre last 1",
      //       "key 1": "last value 1",
      //       "key 2": "pre last 2",
      //       "key 2": "last value 2",
      //     },
      //     5,
      //     6,
      //   ],
      // }
      // """));

      return values.Pop();
    }

    var bytes = Encoding.UTF8.GetBytes(str);
    var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
    {
      CommentHandling = JsonCommentHandling.Skip, // we don't care about comments
      AllowTrailingCommas = true,
      AllowMultipleValues = false,
    });

    return ParseJson(ref reader);
  }

  public static JsonNode? GetKeyIgnoreCase(this JsonNode? obj, params string[] keys)
  {
    if (keys is null || keys.Length == 0)
    {
      return null;
    }

    int idx = 0;
    while (idx < keys.Length)
    {
      if (obj is null || obj.GetValueKind() != JsonValueKind.Object)
      {
        return null;
      }

      var currentObj = obj.AsObject();
      var objDict = currentObj
        .GroupBy(kv => kv.Key.ToUpperInvariant(), kv => (ActualKey: kv.Key, ActualObj: kv.Value)) // upper key <> [list of actual values]
        .ToDictionary(g => g.Key, g => g.ToList());
      var currentKey = keys[idx];
      if (objDict.Count == 0 || !objDict.TryGetValue(currentKey.ToUpperInvariant(), out var objList) || objList.Count == 0)
      {
        return null;
      }

      obj = null;
      foreach (var (actualKey, actualObj) in objList)
      {
        if (string.Equals(currentKey, actualKey, StringComparison.Ordinal))
        {
          obj = actualObj;
          break;
        }
      }

      idx++;
    }

    return obj;
  }

  public enum WebMethod
  {
    Get,
    Post,
  }

  public static async Task<byte[]> WebRequestAsync(string url, WebMethod method, JsonObject? urlParams = null, JsonObject? postData = null, CancellationToken cancelToken = default)
  {
    if (string.IsNullOrEmpty(url))
    {
      throw new ArgumentNullException(nameof(url));
    }

    using var clientConfig = new HttpClientHandler
    {
      AllowAutoRedirect = true,
      CheckCertificateRevocationList = false,
      MaxAutomaticRedirections = 100,
      ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
    };

    if (urlParams is not null && urlParams.Count > 0)
    {
      var uriBuilder = new UriBuilder(url);
      var queryParams = HttpUtility.ParseQueryString(string.Empty);
      foreach (var kv in urlParams)
      {
        if (string.IsNullOrEmpty(kv.Key))
        {
          continue;
        }
        var val = kv.Value?.ToString();
        if (string.IsNullOrEmpty(val))
        {
          continue;
        }
        queryParams[kv.Key] = val;
      }

      uriBuilder.Query = queryParams.ToString();
      url = uriBuilder.ToString();
    }

    using var client = new HttpClient(clientConfig);
    switch (method)
    {
      case WebMethod.Get:
        {
          using var response = await client.GetAsync(url, cancelToken).ConfigureAwait(false);
          response.EnsureSuccessStatusCode();
          return await response.Content.ReadAsByteArrayAsync(cancelToken).ConfigureAwait(false);
        }
      case WebMethod.Post:
        {
          string serializedData = postData is null ? string.Empty : postData.ToJsonString();
          var content = new StringContent(serializedData, Utf8EncodingNoBom, "application/json");
          using var response = await client.PostAsync(url, content, cancelToken).ConfigureAwait(false);
          response.EnsureSuccessStatusCode();
          return await response.Content.ReadAsByteArrayAsync(cancelToken).ConfigureAwait(false);
        }
      default:
        throw new NotImplementedException($"Unknown method {method}");
    }

  }

  public static async Task<Tout[]> ParallelJobsAsync<Tin, Tout>(IEnumerable<Tin> inputs, Func<Tin, int, uint, CancellationToken, Task<Tout>> job, int maxParallelJobs = int.MaxValue, uint jobTrialsOnFailure = 0, CancellationToken cancelToken = default)
  {
    if (inputs is null || !inputs.Any() || job is null)
    {
      return [];
    }

    var options = new ParallelOptions
    {
      CancellationToken = cancelToken,
      MaxDegreeOfParallelism = maxParallelJobs,
    };

    var inputsList = inputs.ToList();
    var res = new Tout[inputsList.Count];
    var tasks = Parallel.ForAsync(0, inputsList.Count, options, async (jobIdx, ct) =>
    {
      uint attempts = jobTrialsOnFailure + 1; // +1 for the first normal run
      for (uint attemptIdx = 0; attemptIdx < attempts && !cancelToken.IsCancellationRequested; attemptIdx++)
      {
        try
        {
          var itemRes = await job(inputsList[jobIdx], jobIdx, attemptIdx, cancelToken).ConfigureAwait(false);
          res[jobIdx] = itemRes;
          break; // exit on success
        }
        catch
        {

        }
      }
    });

    await tasks.ConfigureAwait(false);
    return res;
  }

  public static Task ParallelJobsAsync<Tin>(IEnumerable<Tin> inputs, Func<Tin, int, uint, CancellationToken, Task> job, int maxParallelJobs = int.MaxValue, uint jobTrialsOnFailure = 0, CancellationToken cancelToken = default)
  {
    return ParallelJobsAsync<Tin, object?>(inputs, async (item, jobIdx, trialIdx, cancelToken) =>
    {
      await job(item, jobIdx, trialIdx, cancelToken).ConfigureAwait(false);
      return null;
    }, maxParallelJobs, jobTrialsOnFailure, cancelToken);
  }

  public static string GetLastUrlComponent(string url)
  {
    // "qwe/asd/my_pic.jpg/?t=1719426374"
    var allComponents = url.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    var nameComponent = allComponents.LastOrDefault(s => !s.Trim().StartsWith('?'));
    if (nameComponent is null)
    {
      if (allComponents.Length > 0)
      {
        return allComponents.Last();
      }
      return string.Empty;
    }

    // "qwe/asd/my_pic.jpg?t=1719426374"
    int queryIndex = nameComponent.IndexOf('?');
    return queryIndex != -1 ? nameComponent.Substring(0, queryIndex) : nameComponent;
  }

  public static bool ToBoolSafe(this JsonNode? obj)
  {
    if (obj is null)
    {
      return false;
    }

    switch (obj.GetValueKind())
    {
      case JsonValueKind.String:
        {
          var str = obj.ToString();
          if (string.IsNullOrEmpty(str))
          {
            return false;
          }
          return str.Equals("true", StringComparison.OrdinalIgnoreCase)
            || str.Equals("1", StringComparison.Ordinal);
        }
      case JsonValueKind.Number:
        {
          if (double.TryParse(obj.ToString() ?? string.Empty, CultureInfo.InvariantCulture, out var num) && !double.IsNaN(num))
          {
            const double ZERO_THRESHOLD = 1e-10;
            return Math.Abs(num) >= ZERO_THRESHOLD;
          }
        }
        break;
      case JsonValueKind.True: return true;
    }

    return false;
  }

  public static JsonArray ToArraySafe(this JsonNode? obj)
  {
    if (obj is null)
    {
      return [];
    }

    switch (obj.GetValueKind())
    {
      case JsonValueKind.Array: return obj.AsArray();
    }

    return [];
  }

  public static JsonObject ToObjSafe(this JsonNode? obj)
  {
    if (obj is null)
    {
      return new();
    }

    switch (obj.GetValueKind())
    {
      case JsonValueKind.Object: return obj.AsObject();
    }

    return new();
  }

  public static string ToStringSafe(this JsonNode? obj)
  {
    if (obj is null)
    {
      return string.Empty;
    }

    switch (obj.GetValueKind())
    {
      case JsonValueKind.String: return obj.ToString() ?? string.Empty;
    }

    return string.Empty;
  }

  public static double ToNumSafe(this JsonNode? obj)
  {
    if (TryConvertToNum(obj, out var num))
    {
      return num;
    }
    return 0;
  }

  public static bool TryConvertToNum(this JsonNode? obj, out double num)
  {
    if (obj is null)
    {
      num = double.NaN;
      return false;
    }

    switch (obj.GetValueKind())
    {
      case JsonValueKind.String:
      case JsonValueKind.Number:
        {
          if (
            double.TryParse(obj.ToString() ?? string.Empty, CultureInfo.InvariantCulture, out num)
            && !double.IsNaN(num)
          )
          {
            return true;
          }
        }
        break;
      case JsonValueKind.True:
        {
          num = 1;
          return true;
        }
      case JsonValueKind.False:
        {
          num = 0;
          return true;
        }
    }

    num = double.NaN;
    return false;
  }

  public static string SanitizeFilename(string filename)
  {
    if (string.IsNullOrEmpty(filename))
    {
      return string.Empty;
    }

    // Windows: https://github.com/dotnet/runtime/blob/50be35a211536209b3e1d68619f7d2f2bf3caa0a/src/libraries/System.Private.CoreLib/src/System/IO/Path.Windows.cs#L15
    // Linux: https://github.com/dotnet/runtime/blob/50be35a211536209b3e1d68619f7d2f2bf3caa0a/src/libraries/System.Private.CoreLib/src/System/IO/Path.Unix.cs#L12
    // Windows has more invalid chars, we want to use that in case we're on NTFS partition mounted in Linux
    static char[] WinInvalidFileNameChars() =>
    [
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ];

    return new string(filename.Where(c => !WinInvalidFileNameChars().Contains(c)).ToArray());
  }

  public static Tatt? GetEnumAttribute<Tatt, Tenum>(this Tenum val)
    where Tatt: Attribute
    where Tenum : Enum
  {
    var field = typeof(Tenum).GetField(val.ToString());
    if (field is null)
    {
      return null;
    }

    var att = field.GetCustomAttribute<Tatt>();
    return att;
  }

  public static byte[] CompressData(IEnumerable<byte> data)
  {
    if (data is null || !data.Any())
    {
      return [];
    }

    using var memoryStream = new MemoryStream();
    using (var compressStream = new GZipStream(memoryStream, CompressionLevel.SmallestSize))
    {
      compressStream.Write(data.ToArray(), 0, data.Count());
    }

    return memoryStream.ToArray();
  }

  public static byte[] DecompressData(IEnumerable<byte> compressedData)
  {
    if (compressedData is null || !compressedData.Any())
    {
      return [];
    }

    using var decompressedStream = new MemoryStream();
    using (var compressedStream = new MemoryStream(compressedData.ToArray()))
    {
      using var compressStream = new GZipStream(compressedStream, CompressionMode.Decompress);
      compressStream.CopyTo(decompressedStream);
    }
    return decompressedStream.ToArray();
  }

  public static void WriteJson<T>(T? obj, string filepath, bool asciiEscaping = false)
  {
    if (obj is null)
    {
      return;
    }
    ArgumentNullException.ThrowIfNull(filepath);

    using var fs = new FileStream(filepath, new FileStreamOptions
    {
      Access = FileAccess.Write,
      Mode = FileMode.Create,
      Share = FileShare.None,
      Options = FileOptions.Asynchronous,
    });
    using var utf8js = new Utf8JsonWriter(fs, new JsonWriterOptions
    {
      Indented = true,
      Encoder = asciiEscaping ? JavaScriptEncoder.Default : JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    JsonSerializer.Serialize(utf8js, obj, new JsonSerializerOptions
    {
      WriteIndented = true,
      Encoder = asciiEscaping ? JavaScriptEncoder.Default : JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
      NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });
  }

  public static T? LoadJson<T>(string filepath)
  {
    ArgumentNullException.ThrowIfNull(filepath);

    using var fs = new FileStream(filepath, new FileStreamOptions
    {
      Access = FileAccess.Read,
      Mode = FileMode.Open,
      Share = FileShare.Read,
    });
    var obj = JsonSerializer.Deserialize<T>(fs, new JsonSerializerOptions
    {
      AllowTrailingCommas = true,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      ReadCommentHandling = JsonCommentHandling.Skip,
      NumberHandling =
        System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
      PreferredObjectCreationHandling =
        System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,

    });
    return obj;
  }

  public static ulong GetUnixEpoch()
  {
    return (ulong)DateTimeOffset.Now.ToUnixTimeSeconds();
  }

}

