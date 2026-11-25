"""Does Docker Model Runner (llama.cpp) enforce maxItems when the schema reaches it directly?

Calls the server with plain HTTP so no client library can alter the schema. Compares:
  A  response_format json_schema, array with maxItems 3
  B  the same schema with no maxItems (control)
  C  a top-level GBNF grammar with {0,3} repetition
  D  response_format json_schema with maxItems 3 and strict true (the form the .NET client sends)
Prompt invites many items. Counts items per reply.
"""
import json, sys, time, urllib.request

URL = "http://localhost:12434/engines/v1/chat/completions"
MODEL = "docker.io/ai/qwen2.5:7B-Q4_0"
REPEATS = int(sys.argv[1]) if len(sys.argv) > 1 else 10

PROMPT = ("List every problem you can find in this ticket, one per entry. Do not stop early.\n"
          "Ticket: Login fails for some users. The session cookie has no Secure flag. Passwords are logged in debug output. "
          "The reset token never expires. There is no rate limit on /login. The CSRF token is not checked on /logout. "
          "Error messages reveal whether an email exists.")

def schema(max_items):
    arr = {"type": "array", "items": {"type": "string"}}
    if max_items is not None:
        arr["maxItems"] = max_items
    return {"type": "object", "properties": {"problems": arr}, "required": ["problems"], "additionalProperties": False}

GRAMMAR = r'''
root ::= "{" ws "\"problems\"" ws ":" ws "[" ws item ("," ws item){0,2} ws "]" ws "}"
item ::= "\"" [^"\\\n]{1,200} "\""
ws ::= [ \t\n]*
'''

def call(extra):
    body = {"model": MODEL, "temperature": 0.2, "max_tokens": 600,
            "messages": [{"role": "user", "content": PROMPT}]}
    body.update(extra)
    req = urllib.request.Request(URL, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"]

def rf(sch, strict):
    js = {"name": "r", "schema": sch}
    if strict is not None:
        js["strict"] = strict
    return {"response_format": {"type": "json_schema", "json_schema": js}}

conditions = {
    "A json_schema maxItems=3": lambda: rf(schema(3), None),
    "B json_schema no maxItems": lambda: rf(schema(None), None),
    "C GBNF grammar {0,2}": lambda: {"grammar": GRAMMAR},
    "D json_schema maxItems=3 strict": lambda: rf(schema(3), True),
}

for name, make in conditions.items():
    counts, bad = [], 0
    t0 = time.time()
    for _ in range(REPEATS):
        try:
            text = call(make())
            counts.append(len(json.loads(text)["problems"]))
        except Exception as e:
            bad += 1
            if bad == 1:
                print(f"  first error in {name}: {type(e).__name__} {str(e)[:120]}")
    over = sum(1 for c in counts if c > 3)
    print(f"{name:34s} parsed {len(counts)}/{REPEATS}  counts {counts}  over 3: {over}  ({time.time()-t0:.0f}s)")
