"""Why did the earlier .NET test see 4 or 5 items with maxItems 3? Try the shapes that client sends.

E  array of objects, $schema key, nullable type arrays, additionalProperties false, maxItems 3 (mimics AIJsonUtilities output)
F  the same with no maxItems (control)
G  array of strings with maxItems 3 and maxLength 60 on each string
H  array of objects with maxItems 3 but the objects use $defs and $ref
"""
import json, sys, time, urllib.request

URL = "http://localhost:12434/engines/v1/chat/completions"
MODEL = "docker.io/ai/qwen2.5:7B-Q4_0"
REPEATS = int(sys.argv[1]) if len(sys.argv) > 1 else 8

PROMPT = ("List every authentication and session problem you can find, one finding per entry. Do not stop early.\n"
          "Ticket: Login fails for some users. The session cookie has no Secure flag. Passwords are logged in debug output. "
          "The reset token never expires. There is no rate limit on /login. The CSRF token is not checked on /logout. "
          "Error messages reveal whether an email exists. Sessions are not invalidated on password change.")

finding = {"type": "object", "properties": {"title": {"type": "string"}, "evidence": {"type": ["string", "null"]}}, "required": ["title", "evidence"], "additionalProperties": False}

def wrap(findings_prop, extra=None):
    s = {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
         "properties": {"status": {"type": "string", "enum": ["answered", "cannot_answer"]},
                        "reason": {"type": ["string", "null"]},
                        "findings": findings_prop,
                        "summary": {"type": "string"}},
         "required": ["status", "reason", "findings", "summary"], "additionalProperties": False}
    if extra:
        s.update(extra)
    return s

arr = lambda **kw: {"type": "array", "items": finding, **kw}
conditions = {
    "E objects, maxItems 3": wrap(arr(maxItems=3)),
    "F objects, no maxItems": wrap(arr()),
    "G strings, maxItems 3, maxLength 60": wrap({"type": "array", "items": {"type": "string", "maxLength": 60}, "maxItems": 3}),
    "H objects via $defs/$ref, maxItems 3": wrap({"type": "array", "items": {"$ref": "#/$defs/finding"}, "maxItems": 3}, {"$defs": {"finding": finding}}),
}

def call(sch):
    body = {"model": MODEL, "temperature": 0.2, "max_tokens": 700,
            "messages": [{"role": "user", "content": PROMPT}],
            "response_format": {"type": "json_schema", "json_schema": {"name": "r", "schema": sch}}}
    req = urllib.request.Request(URL, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as r:
        d = json.loads(r.read())
    return d["choices"][0]["message"]["content"], d["choices"][0].get("finish_reason")

for name, sch in conditions.items():
    counts, trunc, longest = [], 0, 0
    t0 = time.time()
    for _ in range(REPEATS):
        text, fin = call(sch)
        if fin == "length": trunc += 1
        try:
            obj = json.loads(text)
            counts.append(len(obj["findings"]))
            if name.startswith("G"):
                longest = max([longest] + [len(x) for x in obj["findings"]])
        except Exception:
            pass
    extra = f"  longest string {longest}" if name.startswith("G") else ""
    print(f"{name:40s} parsed {len(counts)}/{REPEATS} counts {counts} over 3: {sum(c > 3 for c in counts)} cut off by token limit: {trunc}{extra} ({time.time()-t0:.0f}s)", flush=True)
