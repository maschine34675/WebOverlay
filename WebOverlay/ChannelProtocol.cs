using System;
using System.Text;

namespace WebOverlay
{
    /// <summary>
    /// The framing behind named channels and request/reply.
    ///
    /// Every consumer of this library independently invented "prefix:payload"
    /// and hand-wrote the page half of it, which is why this exists. What it
    /// deliberately does not do is take over the plain string API: a message
    /// that is not a well-formed envelope reaches
    /// <see cref="IWebOverlay.MessageReceived"/> exactly as it was sent, so
    /// hand-written pages and older consumers keep working untouched.
    ///
    /// The envelope is JSON with a reserved marker, `__wo`, plus a kind
    /// ("m" message, "q" request, "a" answer), the channel, the payload and -
    /// for a request and its answer - an id. A page must not send JSON with a
    /// top-level `__wo` of its own; that name is the reservation.
    /// </summary>
    internal static class ChannelProtocol
    {
        public const int Version = 1;

        public const string KindMessage = "m";
        public const string KindRequest = "q";
        public const string KindAnswer = "a";

        /// <summary>
        /// Runs before any page script, on every document, so `window.overlay`
        /// is there by the time a page wants it. It only wraps the existing
        /// message bridge - it grants a page nothing the bridge did not
        /// already give it, and the source filter still decides who may talk.
        /// </summary>
        public const string Shim = @"(function () {
  if (!window.chrome || !window.chrome.webview || window.overlay) return;
  var handlers = {}, responders = {}, pending = {}, next = 1;
  function send(o) { window.chrome.webview.postMessage(JSON.stringify(o)); }
  function text(v) { return v === null || v === undefined ? null : String(v); }
  window.overlay = {
    on: function (channel, fn) {
      (handlers[channel] = handlers[channel] || []).push(fn);
    },
    off: function (channel, fn) {
      var list = handlers[channel];
      if (!list) return;
      var i = list.indexOf(fn);
      if (i >= 0) list.splice(i, 1);
    },
    send: function (channel, payload) {
      send({ __wo: 1, t: 'm', c: String(channel), p: text(payload) });
    },
    onRequest: function (channel, fn) { responders[channel] = fn; },
    request: function (channel, payload, timeoutMs) {
      return new Promise(function (resolve) {
        var id = next++;
        pending[id] = resolve;
        send({ __wo: 1, t: 'q', c: String(channel), p: text(payload), i: id });
        setTimeout(function () {
          if (!pending[id]) return;
          delete pending[id];
          resolve(null);
        }, timeoutMs > 0 ? timeoutMs : 5000);
      });
    }
  };
  window.chrome.webview.addEventListener('message', function (e) {
    var m;
    try { m = JSON.parse(String(e.data)); } catch (x) { return; }
    if (!m || m.__wo !== 1) return;
    if (m.t === 'm') {
      var list = handlers[m.c] || [];
      for (var i = 0; i < list.length; i++) {
        try { list[i](m.p); } catch (x) { }
      }
    } else if (m.t === 'q') {
      var fn = responders[m.c];
      Promise.resolve().then(function () { return fn ? fn(m.p) : null; }).then(
        function (v) { send({ __wo: 1, t: 'a', c: m.c, p: text(v), i: m.i }); },
        function () { send({ __wo: 1, t: 'a', c: m.c, p: null, i: m.i }); });
    } else if (m.t === 'a') {
      var resolve = pending[m.i];
      if (resolve) { delete pending[m.i]; resolve(m.p); }
    }
  });
})();";

        public static string Message(string channel, string payload) =>
            build(KindMessage, channel, payload, 0);

        public static string Request(string channel, string payload, int id) =>
            build(KindRequest, channel, payload, id);

        public static string Answer(string channel, string payload, int id) =>
            build(KindAnswer, channel, payload, id);

        private static string build(string kind, string channel, string payload, int id)
        {
            var text = new StringBuilder(64);
            text.Append("{\"__wo\":").Append(Version)
                .Append(",\"t\":\"").Append(kind)
                .Append("\",\"c\":");
            appendString(text, channel);
            text.Append(",\"p\":");
            appendString(text, payload);
            if (id != 0)
                text.Append(",\"i\":").Append(id);
            return text.Append('}').ToString();
        }

        private static void appendString(StringBuilder text, string value)
        {
            if (value == null)
            {
                text.Append("null");
                return;
            }
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        // Control characters and the line separators JSON
                        // parsers disagree about; everything else goes through
                        // as itself, so payloads stay readable in the log.
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            text.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }

        /// <summary>
        /// Recognises an envelope, and nothing else. Anything the least bit
        /// off - a nested object, a missing marker, a stray token - is not an
        /// envelope, and the caller passes the message on untouched rather
        /// than guessing. Only the fields this protocol defines exist, so a
        /// full JSON reader would be more than is needed here and more to get
        /// wrong.
        /// </summary>
        public static bool TryParse(string text, out string kind, out string channel, out string payload, out int id)
        {
            kind = null;
            channel = null;
            payload = null;
            id = 0;
            if (text == null || text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}')
                return false;
            if (text.IndexOf("\"__wo\"", StringComparison.Ordinal) < 0)
                return false;

            bool marked = false;
            int at = 1;
            skipSpace(text, ref at);
            if (at < text.Length && text[at] == '}')
                return false;

            while (at < text.Length)
            {
                skipSpace(text, ref at);
                if (!tryReadString(text, ref at, out string name))
                    return false;
                skipSpace(text, ref at);
                if (at >= text.Length || text[at] != ':')
                    return false;
                at++;
                skipSpace(text, ref at);
                if (at >= text.Length)
                    return false;

                switch (name)
                {
                    case "__wo":
                        if (!tryReadInt(text, ref at, out int version) || version != Version)
                            return false;
                        marked = true;
                        break;
                    case "t":
                        if (!tryReadString(text, ref at, out kind))
                            return false;
                        break;
                    case "c":
                        if (!tryReadString(text, ref at, out channel))
                            return false;
                        break;
                    case "p":
                        if (isNull(text, ref at))
                            payload = null;
                        else if (!tryReadString(text, ref at, out payload))
                            return false;
                        break;
                    case "i":
                        if (!tryReadInt(text, ref at, out id))
                            return false;
                        break;
                    default:
                        return false;
                }

                skipSpace(text, ref at);
                if (at < text.Length && text[at] == ',')
                {
                    at++;
                    continue;
                }
                if (at == text.Length - 1 && text[at] == '}')
                    break;
                return false;
            }

            return marked
                && (kind == KindMessage || kind == KindRequest || kind == KindAnswer)
                && channel != null
                && (kind == KindMessage || id != 0);
        }

        private static void skipSpace(string text, ref int at)
        {
            while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r'))
                at++;
        }

        private static bool isNull(string text, ref int at)
        {
            if (string.CompareOrdinal(text, at, "null", 0, 4) != 0)
                return false;
            at += 4;
            return true;
        }

        private static bool tryReadInt(string text, ref int at, out int value)
        {
            value = 0;
            int start = at;
            while (at < text.Length && text[at] >= '0' && text[at] <= '9')
                at++;
            return at > start
                && at - start < 10
                && int.TryParse(text.Substring(start, at - start), out value);
        }

        private static bool tryReadString(string text, ref int at, out string value)
        {
            value = null;
            if (at >= text.Length || text[at] != '"')
                return false;
            at++;
            var result = new StringBuilder(16);
            while (at < text.Length)
            {
                char c = text[at++];
                if (c == '"')
                {
                    value = result.ToString();
                    return true;
                }
                if (c != '\\')
                {
                    result.Append(c);
                    continue;
                }
                if (at >= text.Length)
                    return false;
                char escaped = text[at++];
                switch (escaped)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (at + 4 > text.Length
                            || !int.TryParse(text.Substring(at, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int code))
                            return false;
                        result.Append((char)code);
                        at += 4;
                        break;
                    default:
                        return false;
                }
            }
            return false;
        }
    }
}
