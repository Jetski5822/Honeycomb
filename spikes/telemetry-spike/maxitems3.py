"""Bisect: the schema the .NET client generates (net-schema.json) versus the shapes that were enforced.
Sends the exact schema over HTTP, then removes one thing at a time.
"""
import copy, json, sys, time, urllib.request, pathlib

URL = "http://localhost:12434/engines/v1/chat/completions"
MODEL = "docker.io/ai/qwen2.5:7B-Q4_0"
REPEATS = int(sys.argv[1]) if len(sys.argv) > 1 else 5
exact = json.loads(pathlib.Path(__file__).with_name("net-schema.json").read_text(encoding="utf-8-sig"))

PROMPT = ("List every authentication and session problem you can find, one finding per entry. Do not stop early.\n"
          "Ticket: Login fails for some users. The session cookie has no Secure flag. Passwords are logged in debug output. "
          "The reset token never expires. There is no rate limit on /login. The CSRF token is not checked on /logout. "
          "Error messages reveal whether an email exists. Sessions are not invalidated on password change.")

def strip_descriptions(o):
    if isinstance(o, dict):
        return {k: strip_descriptions(v) for k, v in o.items() if k != "description"}
    if isinstance(o, list):
        return [strip_descriptions(x) for x in o]
    return o

def add_no_extra(o):
    if isinstance(o, dict):
        o = {k: add_no_extra(v) for k, v in o.items()}
        if o.get("type") == "object":
            o["additionalProperties"] = False
        return o
    if isinstance(o, list):
        return [add_no_extra(x) for x in o]
    return o

v = {}
v["1 exact schema from .NET"] = exact
v["2 exact + additionalProperties false"] = add_no_extra(exact)
v["3 exact without descriptions"] = strip_descriptions(exact)
no_schema_key = copy.deepcopy(exact); no_schema_key.pop("$schema")
v["4 exact without $schema key"] = no_schema_key
no_null = copy.deepcopy(exact); no_null["properties"]["reason"] = {"type": "string"}
v["5 exact with reason as plain string"] = no_null
no_num = copy.deepcopy(exact); no_num["properties"].pop("confidence"); no_num["required"].remove("confidence")
v["6 exact without the number field"] = no_num

def call(sch, system=None):
    msgs = ([{"role": "system", "content": system}] if system else []) + [{"role": "user", "content": PROMPT}]
    body = {"model": MODEL, "temperature": 0.2, "max_tokens": 900, "messages": msgs,
            "response_format": {"type": "json_schema", "json_schema": {"name": "CappedResult", "schema": sch}}}
    req = urllib.request.Request(URL, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=240) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"]

for name, sch in v.items():
    counts = []
    t0 = time.time()
    for _ in range(REPEATS):
        try:
            counts.append(len(json.loads(call(sch))["findings"]))
        except Exception:
            counts.append(-1)
    print(f"{name:40s} counts {counts} over 3: {sum(c > 3 for c in counts)} ({time.time()-t0:.0f}s)", flush=True)
