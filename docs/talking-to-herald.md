# Talking to Herald

Herald speaks whatever other programs on this PC send it. Claude Code does this through the hook Herald installs, but any script or program can do the same: a build that says when it's done, a monitor that reads out alerts, a hotkey tool that reads a selection.

## Finding Herald

Herald listens on **127.0.0.1**, on this PC only. The port is **8766** unless it was changed under **Settings > General > Listen on port**.

While Herald runs, it writes where it listens to a file:

```
%LOCALAPPDATA%\Herald\endpoint.json
```

```json
{
  "host": "127.0.0.1",
  "port": 8766,
  "pid": 12345,
  "version": "1.0.14"
}
```

Read `port` from there, and use 8766 if the file isn't there. If the file is missing, or nothing answers on its port, Herald isn't running.

## Sending a command

1. Open a TCP connection to `127.0.0.1` on that port.
2. Send **one line of JSON**, UTF-8, ending with a newline (`\n`).
3. Read **one line** back. Herald then closes the connection.

One command per connection. Property names don't care about upper or lower case.

```json
{"type": "speak", "sender": "my-script", "text": "The backup finished."}
```

The reply tells you the state Herald is in afterwards:

```json
{"status":"ok","enabled":true,"speed":150}
```

- `enabled`: whether speech is on.
- `speed`: the speaking speed in percent.

A line that isn't JSON gets `{"status":"error"}`, and the reason goes to Herald's log.

## Commands

| `type` | Other fields | What it does |
|---|---|---|
| `speak` | `text`, `sender` (optional) | Adds the text to Herald's queue. It's spoken after what's already queued; it doesn't interrupt. |
| `toggle` | | Turns speech on or off, and says which. |
| `skip` | | Skips the part being spoken; the next part follows. |
| `skipmessage` | | Skips the rest of the message being spoken. |
| `setspeed` | `value`: 50 to 300 | Sets the speed in percent, and says it. |
| `show` | | Brings Herald's window forward. |

An unknown `type` is ignored and noted in Herald's log.

### What happens to spoken text

- **Speech is off:** a `speak` is dropped, not saved for later. The reply's `enabled` shows this.
- **Long text** is split into parts at line breaks and sentence ends, so speaking starts quickly. Parts of one message can be skipped together with `skipmessage`.
- **Markdown** (`**bold**`, `` `code` ``, headings, links) is cleaned up before speaking. Code blocks aren't spoken at all.
- **Very long text** is cut off at 20,000 characters.

## Senders

`sender` is a name you choose, like `my-script` or `build`. It's `unknown` if you leave it out. Each sender gets its own settings under **Settings > Senders**, created the first time it sends something:

- a voice, and other voices for other languages
- mute
- characters to leave out and words to replace
- whether its messages are shown formatted as Markdown
- whether its name is spoken first

The name isn't checked: anything on this PC can send as any sender. Use your own name rather than `claude` or `herald`, so your messages keep their own settings.

## Examples

Each of these speaks "The backup finished." as the sender `my-script`, and prints Herald's reply.

### PowerShell

```powershell
$endpointFile = Join-Path $env:LOCALAPPDATA "Herald\endpoint.json"
$port = 8766
if (Test-Path $endpointFile) { $port = (Get-Content -Raw $endpointFile | ConvertFrom-Json).port }

$client = New-Object System.Net.Sockets.TcpClient("127.0.0.1", $port)
$stream = $client.GetStream()
$line = (@{ type = "speak"; sender = "my-script"; text = "The backup finished." } | ConvertTo-Json -Compress) + "`n"
$bytes = [System.Text.Encoding]::UTF8.GetBytes($line)
$stream.Write($bytes, 0, $bytes.Length)
(New-Object System.IO.StreamReader($stream)).ReadLine()
$client.Close()
```

### Python

```python
import json, os, socket

def herald_port():
    try:
        with open(os.path.join(os.environ["LOCALAPPDATA"], "Herald", "endpoint.json"), encoding="utf-8") as f:
            return json.load(f)["port"]
    except (OSError, ValueError, KeyError):
        return 8766

def herald(command):
    with socket.create_connection(("127.0.0.1", herald_port()), timeout=5) as connection:
        connection.sendall((json.dumps(command) + "\n").encode("utf-8"))
        return json.loads(connection.makefile(encoding="utf-8").readline())

print(herald({"type": "speak", "sender": "my-script", "text": "The backup finished."}))
```

### Node.js

```javascript
const fs = require("fs"), net = require("net"), path = require("path");

function heraldPort() {
  try {
    return JSON.parse(fs.readFileSync(path.join(process.env.LOCALAPPDATA, "Herald", "endpoint.json"), "utf8")).port;
  } catch {
    return 8766;
  }
}

function herald(command) {
  return new Promise((resolve, reject) => {
    const connection = net.createConnection(heraldPort(), "127.0.0.1", () => connection.write(JSON.stringify(command) + "\n"));
    let reply = "";
    connection.setEncoding("utf8");
    connection.on("data", chunk => reply += chunk);
    connection.on("end", () => resolve(JSON.parse(reply)));
    connection.on("error", reject);
  });
}

herald({ type: "speak", sender: "my-script", text: "The backup finished." }).then(console.log);
```

### C#

```csharp
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static int HeraldPort()
{
    try
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herald", "endpoint.json");
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("port").GetInt32();
    }
    catch
    {
        return 8766;
    }
}

using var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", HeraldPort());
using var stream = client.GetStream();
var line = new JsonObject { ["type"] = "speak", ["sender"] = "my-script", ["text"] = "The backup finished." }.ToJsonString() + "\n";
await stream.WriteAsync(Encoding.UTF8.GetBytes(line));
Console.WriteLine(await new StreamReader(stream).ReadLineAsync());
```

## When something doesn't work

- **Nothing answers:** Herald isn't running, or it couldn't open its port. Its main window then says why at the bottom, for example that another program uses the port.
- **The reply says `"enabled":false`:** speech is off. Turn it on with the button in Herald, Alt+Shift+S, or `toggle`.
- **Nothing is heard but the reply is fine:** check whether the sender is muted under Settings > Senders.
- **Herald's log** (`%LOCALAPPDATA%\Herald\herald.log`, also on the Files tab in Settings) notes messages it couldn't read, unknown commands, and voices that failed.
