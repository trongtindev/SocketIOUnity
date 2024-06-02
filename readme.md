# SocketIOUnity

A Wrapper for [socket.io-client-csharp](https://github.com/doghappy/socket.io-client-csharp) to work with Unity,
Supports socket.io server v2/v3/v4, and has implemented http polling and websocket.

| Library                                                                        | Version |
| ------------------------------------------------------------------------------ | ------- |
| [socket.io-client-csharp](https://github.com/doghappy/socket.io-client-csharp) | 3.0.8   |

## Supported Platforms

## Usage

Check the 'Samples~' folder and [socket.io-client-csharp](https://github.com/doghappy/socket.io-client-csharp) repo for more usage info.
💻 PC/Mac, 🍎 iOS, 🤖 Android

### Initiation:

You may want to put the script on the Camera Object or using `DontDestroyOnLoad` to keep the socket alive between scenes!

```csharp
var uri = new Uri("https://www.example.com");
socket = new SocketIOUnity(uri, new SocketIOOptions
{
    Query = new Dictionary<string, string>
      {
          {"token", "UNITY" }
      }
    ,
    Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
});
```

### JsonSerializer:

The library uses System.Text.Json to serialize and deserialize json by default, may [won't work in the current il2cpp](https://forum.unity.com/threads/please-add-system-text-json-support.1000369/).
You can use Newtonsoft Json.Net instead:

```csharp
socket.JsonSerializer = new NewtonsoftJsonSerializer();
```

## Acknowledgement

[socket.io-client-csharp](https://github.com/doghappy/socket.io-client-csharp)

[Socket.IO](https://github.com/socketio/socket.io)

[System.Text.Json](https://docs.microsoft.com/en-us/dotnet/api/system.text.json)

[Newtonsoft Json.NET](https://www.newtonsoft.com/json/help/html/Introduction.htm)

[Unity Documentation](https://docs.unity.com)
