#!/usr/bin/env python3
"""DataspaceDrive Spike - Ablauf Anbieter -> Empfaenger, Gegenprobe mit einem dritten Teilnehmer.

Beantwortet die Spike-Fragen aus docs/konzept-dataspace-drive-de.md mit Messwerten:

  S1  CX-Usage-Policy (FrameworkAgreement + UsagePurpose): Anlage und Verhandlung
  S2  Sichtbarkeit: Empfaenger sieht nur seine Zustellungen, Gegenprobe nur ihre und kann fremde Offers nicht nutzen
  S3  Katalogfilter auf eigene Asset-Properties (drive:kind, drive:recipientBpn) ueber DSP 2025-1
  S4  Header am Backend: Zustell-Token, Edc-Bpn, Edc-Contract-Agreement-Id; keine Consumer-Header; Pfade
  S5  EDR-Ablauf mit unveraendert uebernommenem Offer, Latenzen
  S6  Grosse Datei, parallele Downloads (Threadpool der Public API), segmentierter Abruf (offset/length)
  S7  Widerruf: Agreement-Retirement waehrend eines laufenden Downloads, danach Token und Neuverhandlung
  S8  NetworkPolicy: Content-Port der Quelle ist fuer fremde Pods gesperrt
  S9  Asset mit Agreements loeschen (erwartet: 409)

Laeuft als Pod im Namespace des Empfaengers, weil die Management-APIs nur clusterintern
erreichbar sind. Nur Standardbibliothek.
"""
import base64
import concurrent.futures
import hashlib
import hmac
import json
import os
import socket
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

EDC = "https://w3id.org/edc/v0.0.1/ns/"
ODRL = "http://www.w3.org/ns/odrl/2/"
TX = "https://w3id.org/tractusx/v0.0.1/ns/"
DCT = "http://purl.org/dc/terms/"
CX = "https://w3id.org/catenax/2025/9/policy/"
DRIVE = "https://ns.swms-cloud.com/drive/v1/"
ODRL_PROFILE = "https://w3id.org/dspace/2025/1/odrl-profile.jsonld"
MIB = 1024 * 1024

env = os.environ.get
RUN = env("RUN_ID")
SIDES = {
    "provider": {"mgmt": env("PROV_MGMT"), "key": env("PROV_KEY")},
    "consumer": {"mgmt": env("CONS_MGMT"), "key": env("CONS_KEY")},
    "negative": {"mgmt": env("NEG_MGMT"), "key": env("NEG_KEY")},
}
PROV_BPN, PROV_DID, PROV_DSP = env("PROV_BPN"), env("PROV_DID"), env("PROV_DSP")
CONS_BPN, NEG_BPN = env("CONS_BPN"), env("NEG_BPN")
SOURCE_CONTENT, SOURCE_ADMIN = env("SOURCE_CONTENT"), env("SOURCE_ADMIN")
DELIVERY_KEY = env("DELIVERY_KEY").encode()
LARGE_MB = int(env("LARGE_MB", "1024"))
PAR_MB = int(env("PAR_MB", "128"))
PAR_COUNT = int(env("PAR_COUNT", "12"))
CLEANUP = env("CLEANUP", "1") == "1"

T0 = time.time()
RESULTS: dict = {}
CREATED = {"contractdefinitions": [], "policydefinitions": [], "assets": []}
CONSUMER_TPS: list = []


def log(msg: str):
    print(f"[{time.time() - T0:7.1f}s] {msg}", flush=True)


def result(sid: str, status: str, summary: str, **data):
    RESULTS.setdefault(sid, []).append({"status": status, "summary": summary, **data})
    log(f"  >> {sid} {status}: {summary}")


# ---------------------------------------------------------------- HTTP und JSON-LD-Hilfen

def _json(raw: str):
    try:
        return json.loads(raw) if raw else None
    except ValueError:
        return raw[:500]


def call(method, url, body=None, key=None, headers=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if key:
        req.add_header("X-Api-Key", key)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, _json(r.read().decode("utf-8", "replace"))
    except urllib.error.HTTPError as e:
        return e.code, _json(e.read().decode("utf-8", "replace"))
    except Exception as e:  # noqa: BLE001 - jeder Fehler ist ein Messergebnis
        return 0, repr(e)[:300]


def mgmt(side, method, path, body=None, timeout=60):
    s = SIDES[side]
    return call(method, s["mgmt"].rstrip("/") + path, body, key=s["key"], timeout=timeout)


def pick(obj, local):
    """Liest eine Property egal ob kompakt, mit Praefix oder als volle IRI."""
    if not isinstance(obj, dict):
        return None
    for k, v in obj.items():
        if k == local or k.endswith(":" + local) or k.endswith("/" + local):
            if isinstance(v, dict) and "@value" in v:
                return v["@value"]
            if isinstance(v, list) and len(v) == 1:
                x = v[0]
                return x.get("@value", x.get("@id", x)) if isinstance(x, dict) else x
            return v
    return None


def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def merge_ctx(ctx):
    base = {"@vocab": EDC, "odrl": ODRL}
    if isinstance(ctx, dict):
        return {**ctx, **base}
    if isinstance(ctx, list):
        return ctx + [base]
    if isinstance(ctx, str):
        return [ctx, base]
    return base


# ---------------------------------------------------------------- Testdaten

def _pattern_block() -> bytes:
    seed = b"dataspacedrive-spike"
    return b"".join(hashlib.sha256(seed + i.to_bytes(8, "big")).digest() for i in range(MIB // 32))


BLOCK = _pattern_block()


def expected_sha(start: int, end: int) -> str:
    h = hashlib.sha256()
    pos = start
    while pos < end:
        off = pos % MIB
        take = min(end - pos, MIB - off)
        h.update(BLOCK[off:off + take])
        pos += take
    return h.hexdigest()


def delivery_token(delivery_id: str) -> str:
    mac = hmac.new(DELIVERY_KEY, delivery_id.encode(), hashlib.sha256).digest()
    return "v1." + base64.urlsafe_b64encode(mac).rstrip(b"=").decode()


CASES = {
    "smalla": {"size": 1 * MIB, "recipient": CONS_BPN, "usage": "cx", "title": "Spike CX-Profil"},
    "smallb": {"size": 1 * MIB, "recipient": CONS_BPN, "usage": "mem",
               "title": "Prüfbericht Spike – Umlaute äöüß"},
    "neg": {"size": 1 * MIB, "recipient": NEG_BPN, "usage": "mem", "title": "Spike an die Gegenprobe"},
    "large": {"size": LARGE_MB * MIB, "recipient": CONS_BPN, "usage": "mem", "title": "Spike gross"},
    "par": {"size": PAR_MB * MIB, "recipient": CONS_BPN, "usage": "mem", "title": "Spike parallel"},
    "seg": {"size": 32 * MIB, "recipient": CONS_BPN, "usage": "mem", "title": "Spike Segmente"},
    "slow": {"size": 32 * MIB, "recipient": CONS_BPN, "usage": "mem", "title": "Spike Widerruf"},
}


def delivery_id(case):
    return f"spike{RUN}-{case}"


def asset_id(case):
    return f"drive-spike-{RUN}-{case}"


AP = {CONS_BPN: f"drive-spike-{RUN}-ap-cons", NEG_BPN: f"drive-spike-{RUN}-ap-neg"}
UP = {"cx": f"drive-spike-{RUN}-usage-cx", "mem": f"drive-spike-{RUN}-usage-mem"}


def cd_id(case):
    return f"drive-spike-{RUN}-cd-{case}"


# ---------------------------------------------------------------- EDC-Bausteine

def access_policy(pid, bpns):
    return {
        "@context": [ODRL_PROFILE, {"@vocab": EDC}], "@type": "PolicyDefinition", "@id": pid,
        "policy": {"@type": "Set", "permission": [{
            "action": CX + "access",
            "constraint": {"@type": "LogicalConstraint", "and": [
                {"leftOperand": CX + "Membership", "operator": "eq", "rightOperand": "active"},
                {"leftOperand": CX + "BusinessPartnerNumber", "operator": "isAnyOf", "rightOperand": bpns},
            ]}}]}}


def cx_usage_permissions():
    return [{
        "action": ODRL + "use",
        "constraint": {"@type": "LogicalConstraint", "and": [
            {"leftOperand": CX + "FrameworkAgreement", "operator": "eq", "rightOperand": "DataExchangeGovernance:1.0"},
            {"leftOperand": CX + "UsagePurpose", "operator": "isAnyOf", "rightOperand": ["windx.drive.fileExchange:1"]},
        ]}}]


def usage_policy_cx(pid):
    return {"@context": [ODRL_PROFILE, {"@vocab": EDC}], "@type": "PolicyDefinition", "@id": pid,
            "policy": {"@type": "Set", "permission": cx_usage_permissions()}}


MEMBERSHIP = {"leftOperand": CX + "Membership", "operator": "eq", "rightOperand": "active"}

# Formen der Contract-Policy fuer den Probe-Schritt. Ins Agreement geht nur die Contract-Policy; die
# BPN-Einschraenkung steckt in der Access-Policy und wird bei der Verhandlung erneut geprueft.
CONTRACT_VARIANTS = {
    "atomic": {"constraint": MEMBERSHIP},
    "list": {"constraint": [MEMBERSHIP]},
    "andnotype": {"constraint": {"and": [MEMBERSHIP]}},
    # Muster aus tractus-wind-x-edc/deployment/windx-catenax/windx-cx-seed.sh
    "and": {"constraint": {"@type": "LogicalConstraint", "and": [MEMBERSHIP]}},
    "open": {},
}


STATE = {"contract_variant": "and"}


def contract_permissions(variant):
    return [{"action": CX + "access", **CONTRACT_VARIANTS[variant]}]


def contract_policy(pid, variant):
    return {"@context": [ODRL_PROFILE, {"@vocab": EDC}], "@type": "PolicyDefinition", "@id": pid,
            "policy": {"@type": "Set", "permission": contract_permissions(variant)}}


def expected_permissions(case):
    """Was der Empfaenger aus dem Austauschprofil ueber die Contract-Policy eines Falls weiss."""
    if CASES[case]["usage"] == "cx":
        return cx_usage_permissions()
    return contract_permissions(STATE["contract_variant"])


def asset_body(case):
    c = CASES[case]
    did = delivery_id(case)
    manifest = [{"id": "1", "name": f"{c['title']}.bin", "size": c["size"],
                 "sha256": expected_sha(0, c["size"]), "contentType": "application/octet-stream"}]
    return {
        "@context": {"@vocab": EDC, "dct": DCT, "drive": DRIVE}, "@type": "Asset", "@id": asset_id(case),
        "properties": {
            "name": c["title"], "dct:title": c["title"], "contenttype": "application/octet-stream",
            "drive:kind": "delivery-spike", "drive:spikeRun": RUN, "drive:schemaVersion": "1",
            "drive:recipientBpn": c["recipient"], "drive:senderBpn": PROV_BPN,
            "drive:totalSize": str(c["size"]), "drive:itemCount": "1",
            "drive:manifest": json.dumps(manifest, ensure_ascii=True),
        },
        "privateProperties": {"drive:deliveryId": did},
        "dataAddress": {
            "@type": "DataAddress", "type": "HttpData",
            "baseUrl": f"{SOURCE_CONTENT}/edc/deliveries/{did}",
            "proxyPath": "true", "proxyQueryParams": "true", "proxyMethod": "false", "proxyBody": "false",
            "authKey": "X-Drive-Delivery-Token", "authCode": delivery_token(did),
        },
    }


def create(side, path, body, label):
    st, resp = mgmt(side, "POST", path, body)
    ok = st in (200, 201, 204, 409)
    log(f"  POST {path} {label} -> {st}" + ("" if ok else f" | {str(resp)[:400]}"))
    return st, resp


def discover(side):
    attempts = [
        ("legacy-bpnl", {"@context": {"tx": TX, "edc": EDC},
                         "tx:bpnl": PROV_BPN, "edc:counterPartyAddress": PROV_DSP}),
        ("counterPartyId", {"@context": {"tx": TX, "edc": EDC}, "@type": "tx:ConnectorParamsDiscoveryRequest",
                            "edc:counterPartyId": PROV_DID, "edc:counterPartyAddress": PROV_DSP}),
    ]
    found, info = None, {}
    for name, body in attempts:
        st, r = mgmt(side, "POST", "/v4alpha/connectordiscovery/dspversionparams", body)
        proto = pick(r, "protocol")
        info[name] = {"status": st, "protocol": proto, "counterPartyId": pick(r, "counterPartyId"),
                      "counterPartyAddress": pick(r, "counterPartyAddress"),
                      "error": None if proto else str(r)[:200]}
        if proto and not found:
            found = {"protocol": proto, "counterPartyAddress": pick(r, "counterPartyAddress") or PROV_DSP,
                     "counterPartyId": pick(r, "counterPartyId") or PROV_BPN}
    return found or {"protocol": "dataspace-protocol-http", "counterPartyAddress": PROV_DSP,
                     "counterPartyId": PROV_BPN}, info


def catalog(side, dsp, filters=None):
    body = {"@context": {"@vocab": EDC}, "@type": "CatalogRequest",
            "counterPartyAddress": dsp["counterPartyAddress"], "counterPartyId": dsp["counterPartyId"],
            "protocol": dsp["protocol"]}
    if filters is not None:
        body["querySpec"] = {"@type": "QuerySpec", "offset": 0, "limit": 200, "filterExpression": [
            {"@type": "Criterion", "operandLeft": k, "operator": "=", "operandRight": v} for k, v in filters]}
    st, cat = mgmt(side, "POST", "/v3/catalog/request", body, timeout=90)
    datasets = []
    if isinstance(cat, dict):
        datasets = [d for d in as_list(cat.get("dcat:dataset") or cat.get("dataset") or pick(cat, "dataset"))
                    if isinstance(d, dict)]
    return st, cat, datasets


def run_filters(extra=None):
    f = [(DRIVE + "kind", "delivery-spike"), (DRIVE + "spikeRun", RUN)]
    return f + (extra or [])


def wait_negotiation(side, nid, limit=120):
    t, neg = time.time(), None
    while time.time() - t < limit:
        st, neg = mgmt(side, "GET", f"/v3/contractnegotiations/{urllib.parse.quote(nid)}")
        state = pick(neg, "state")
        if state in ("FINALIZED", "TERMINATED"):
            return state, neg
        time.sleep(2)
    return "TIMEOUT", neg


CX_CONTEXT = "https://w3id.org/catenax/2025/9/policy/context.jsonld"


def negotiate(side, dsp, ds, cat_ctx, asset, expected_permissions=None):
    """Verhandelt ein Katalog-Angebot ueber /v3/edrs und probiert dabei mehrere Request-Formen.

    Der Empfaenger-EDC vergleicht die Agreement-Policy des Anbieters per Jackson-Baum mit der Policy aus
    unserem Request (PolicyEquality: alles ausser @type, assignee, target). Jede Abweichung in der
    JSON-LD-Expansion fuehrt zu "Policy in the contract agreement is not equal to the one in the
    contract offer". Wer das Austauschprofil kennt, kann die Policy in genau der Form senden, in der der
    Anbieter sie angelegt hat (expected_permissions), und uebernimmt aus dem Katalog nur die Offer-ID.
    """
    offers = as_list(ds.get("odrl:hasPolicy") or ds.get("hasPolicy") or pick(ds, "hasPolicy"))
    if not offers:
        return {"ok": False, "attempts": [{"error": "kein Offer im Dataset"}]}
    offer = offers[0]
    base = {"@id": offer.get("@id"), "@type": "odrl:Offer",
            "odrl:assigner": {"@id": dsp["counterPartyId"]}, "odrl:target": {"@id": asset}}
    compact = {k: v for k, v in offer.items() if k not in ("@id", "@type")
               and not (k in ("odrl:prohibition", "odrl:obligation", "prohibition", "obligation") and not v)}
    trimmed_ctx = [c for c in as_list(cat_ctx) if c in (CX_CONTEXT, "https://w3id.org/dspace/2025/1/context.jsonld")]
    variants = []
    if expected_permissions is not None:
        variants.append(("profile-policy", [ODRL_PROFILE, {"@vocab": EDC, "odrl": ODRL}],
                         {**base, "permission": expected_permissions}))
    variants += [
        ("offer-profile-context", [ODRL_PROFILE, CX_CONTEXT, {"@vocab": EDC, "odrl": ODRL}], {**base, **compact}),
        ("offer-catalog-context-trimmed", trimmed_ctx + [{"@vocab": EDC, "odrl": ODRL}], {**base, **compact}),
        ("offer-catalog-context", merge_ctx(cat_ctx), {**base, **compact}),
    ]
    attempts = []
    for name, ctx, p in variants:
        t = time.time()
        st, r = mgmt(side, "POST", "/v3/edrs", {
            "@context": ctx, "@type": "ContractRequest", "counterPartyAddress": dsp["counterPartyAddress"],
            "protocol": dsp["protocol"], "policy": p})
        if st not in (200, 201) or not isinstance(r, dict):
            attempts.append({"variant": name, "httpStatus": st, "error": str(r)[:400]})
            continue
        nid = r.get("@id")
        state, neg = wait_negotiation(side, nid)
        attempt = {"variant": name, "httpStatus": st, "negotiationId": nid, "state": state,
                   "seconds": round(time.time() - t, 1), "errorDetail": pick(neg, "errorDetail")}
        attempts.append(attempt)
        if state == "FINALIZED":
            return {"ok": True, "negotiationId": nid, "agreementId": pick(neg, "contractAgreementId"),
                    "seconds": attempt["seconds"], "variant": name, "attempts": attempts}
        # "not fulfilled" heisst: die Policy selbst ist nicht erfuellt, eine andere Form hilft nicht.
        # Ein Gleichheitsfehler erscheint beim Empfaenger dagegen ohne errorDetail.
        if "not fulfilled" in str(attempt["errorDetail"] or ""):
            break
    return {"ok": False, "attempts": attempts}


def get_edr(side, nid, limit=120):
    t = time.time()
    query = {"@context": {"@vocab": EDC}, "@type": "QuerySpec", "limit": 200, "filterExpression": [
        {"operandLeft": "contractNegotiationId", "operator": "=", "operandRight": nid}]}
    while time.time() - t < limit:
        st, r = mgmt(side, "POST", "/v3/edrs/request", query)
        if st != 200:
            st, r = mgmt(side, "POST", "/v3/edrs/request", {"@context": {"@vocab": EDC}, "@type": "QuerySpec",
                                                            "limit": 500})
        entries = [e for e in as_list(r) if isinstance(e, dict) and pick(e, "contractNegotiationId") in (None, nid)]
        if entries:
            tp = pick(entries[0], "transferProcessId") or entries[0].get("@id")
            st2, da = mgmt(side, "GET", f"/v3/edrs/{urllib.parse.quote(tp)}/dataaddress?auto_refresh=true")
            if st2 == 200 and pick(da, "endpoint"):
                CONSUMER_TPS.append(tp)
                return {"transferProcessId": tp, "endpoint": pick(da, "endpoint"),
                        "authorization": pick(da, "authorization"), "seconds": round(time.time() - t, 1)}
        time.sleep(2)
    return None


def download(url, token, extra_headers=None, timeout=60):
    req = urllib.request.Request(url, method="GET")
    if token:
        req.add_header("Authorization", token)
    for k, v in (extra_headers or {}).items():
        req.add_header(k, v)
    started, first, n, h = time.time(), None, 0, hashlib.sha256()
    status, err = 0, None
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            status = r.status
            while True:
                chunk = r.read(256 * 1024)
                if not chunk:
                    break
                if first is None:
                    first = time.time()
                n += len(chunk)
                h.update(chunk)
    except urllib.error.HTTPError as e:
        status, err = e.code, e.read()[:300].decode("utf-8", "replace")
    except Exception as e:  # noqa: BLE001
        err = repr(e)[:300]
    end = time.time()
    return {"status": status, "bytes": n, "sha256": h.hexdigest(), "error": err,
            "start": round(started - T0, 2), "end": round(end - T0, 2), "seconds": round(end - started, 2),
            "ttfb": round(first - started, 2) if first else None,
            "mbps": round(n / MIB / max(end - started, 0.001), 1)}


def source_log(since=0):
    st, r = call("GET", f"{SOURCE_ADMIN}/_spike/requests?since={since}")
    return r if st == 200 and isinstance(r, list) else []


def source_seq():
    entries = source_log(0)
    return max((e["seq"] for e in entries), default=0)


def accept(case, dsp):
    """Empfaengerfluss fuer einen Fall: frischer Katalog -> /v3/edrs -> EDR. Liefert (negotiation, edr)."""
    st, cat, ds = catalog("consumer", dsp, run_filters([(EDC + "id", asset_id(case))]))
    target = next((d for d in ds if d.get("@id") == asset_id(case)), None)
    if not target:
        return {"ok": False, "attempts": [{"error": f"{asset_id(case)} nicht im Katalog (HTTP {st})"}]}, None
    neg = negotiate("consumer", dsp, target, cat.get("@context") if isinstance(cat, dict) else None, asset_id(case),
                    expected_permissions(case))
    if not neg["ok"]:
        return neg, None
    return neg, get_edr("consumer", neg["negotiationId"])


# ---------------------------------------------------------------- Phasen

def phase_preflight():
    log("== P0 Vorabpruefung")
    for side in SIDES:
        st, _ = mgmt(side, "POST", "/v3/assets/request", {"@context": {"@vocab": EDC}, "@type": "QuerySpec",
                                                         "limit": 1})
        log(f"  Management-API {side}: HTTP {st}")
        if st != 200:
            raise SystemExit(f"Management-API {side} nicht erreichbar")
    st, _ = call("GET", f"{SOURCE_ADMIN}/healthz")
    log(f"  Quelle (Admin-Port): HTTP {st}")
    if st != 200:
        raise SystemExit("Spike-Quelle nicht erreichbar")
    call("POST", f"{SOURCE_ADMIN}/_spike/reset", {})


def phase_s8_network_policy():
    log("== S8 NetworkPolicy: direkter Zugriff auf den Content-Port der Quelle")
    host = urllib.parse.urlsplit(SOURCE_CONTENT)
    try:
        with socket.create_connection((host.hostname, host.port or 80), timeout=5):
            result("S8", "FAIL", "Content-Port aus fremdem Pod erreichbar - NetworkPolicy greift nicht")
    except (TimeoutError, socket.timeout):
        result("S8", "PASS", "Verbindung zum Content-Port laeuft in den Timeout (Calico verwirft die Pakete)")
    except OSError as e:
        result("S8", "PASS", f"Verbindung zum Content-Port abgewiesen ({e.__class__.__name__})")


def phase_policy_probe(dsp_bob):
    """Welche Form der Contract-Policy uebersteht die Gleichheitspruefung des Empfaengers unter DSP 2025-1?"""
    log("== S5 Probe: Formen der Contract-Policy")
    ap = f"drive-spike-{RUN}-ap-probe"
    create("provider", "/v3/policydefinitions", access_policy(ap, [CONS_BPN]), ap)
    CREATED["policydefinitions"].append(ap)
    outcomes, chosen = {}, None
    for variant in CONTRACT_VARIANTS:
        pid, aid, cid = (f"drive-spike-{RUN}-cp-{variant}", f"drive-spike-{RUN}-probe-{variant}",
                         f"drive-spike-{RUN}-cd-probe-{variant}")
        st, resp = create("provider", "/v3/policydefinitions", contract_policy(pid, variant), pid)
        if st not in (200, 201, 409):
            outcomes[variant] = {"created": False, "status": st, "response": str(resp)[:300]}
            continue
        CREATED["policydefinitions"].append(pid)
        body = asset_body("smallb")
        body["@id"] = aid
        body["properties"]["drive:kind"] = "probe-spike"
        create("provider", "/v3/assets", body, aid)
        CREATED["assets"].append(aid)
        create("provider", "/v3/contractdefinitions", {
            "@context": {"@vocab": EDC}, "@type": "ContractDefinition", "@id": cid,
            "accessPolicyId": ap, "contractPolicyId": pid,
            "assetsSelector": [{"@type": "Criterion", "operandLeft": EDC + "id", "operator": "=", "operandRight": aid}]},
            cid)
        CREATED["contractdefinitions"].append(cid)
        st, cat, ds = catalog("consumer", dsp_bob, [(EDC + "id", aid)])
        target = next((d for d in ds if d.get("@id") == aid), None)
        if not target:
            outcomes[variant] = {"created": True, "inCatalog": False, "catalogStatus": st}
            continue
        offers = as_list(target.get("odrl:hasPolicy") or target.get("hasPolicy"))
        neg = negotiate("consumer", dsp_bob, target, cat.get("@context"), aid, contract_permissions(variant))
        outcomes[variant] = {"created": True, "inCatalog": True, "finalized": neg["ok"],
                             "catalogOffer": offers[0] if offers else None, "attempts": neg["attempts"]}
        log(f"  Variante {variant}: {'FINALIZED' if neg['ok'] else 'gescheitert'}")
        if neg["ok"] and chosen is None:
            chosen = variant
    result("S5", "PASS" if chosen else "FAIL",
           f"Contract-Policy-Formen: " + ", ".join(
               f"{v}={'ok' if o.get('finalized') else ('nicht anlegbar' if not o.get('created') else 'scheitert')}"
               for v, o in outcomes.items()) + (f"; verwendet wird '{chosen}'" if chosen else ""),
           variants=outcomes)
    return chosen


def phase_provider_setup(contract_variant):
    log("== P1 Anbieter legt Policies, Zustell-Assets und Contract Definitions an")
    for bpn, pid in AP.items():
        st, _ = create("provider", "/v3/policydefinitions", access_policy(pid, [bpn]), pid)
        if st in (200, 201, 409):
            CREATED["policydefinitions"].append(pid)
    st_cx, resp_cx = create("provider", "/v3/policydefinitions", usage_policy_cx(UP["cx"]), UP["cx"])
    cx_ok = st_cx in (200, 201, 409)
    if cx_ok:
        CREATED["policydefinitions"].append(UP["cx"])
        result("S1", "INFO", "CX-Usage-Policy (FrameworkAgreement + UsagePurpose) von der Validierung akzeptiert")
    else:
        result("S1", "FAIL", f"CX-Usage-Policy abgelehnt (HTTP {st_cx})", response=str(resp_cx)[:600])
    st, _ = create("provider", "/v3/policydefinitions", contract_policy(UP["mem"], contract_variant), UP["mem"])
    if st in (200, 201, 409):
        CREATED["policydefinitions"].append(UP["mem"])

    for case, c in CASES.items():
        st, _ = create("provider", "/v3/assets", asset_body(case), asset_id(case))
        if st in (200, 201, 409):
            CREATED["assets"].append(asset_id(case))
        if c["usage"] == "cx" and not cx_ok:
            continue
        body = {"@context": {"@vocab": EDC}, "@type": "ContractDefinition", "@id": cd_id(case),
                "accessPolicyId": AP[c["recipient"]], "contractPolicyId": UP[c["usage"]],
                "assetsSelector": [{"@type": "Criterion", "operandLeft": EDC + "id", "operator": "=",
                                    "operandRight": asset_id(case)}]}
        st, _ = create("provider", "/v3/contractdefinitions", body, cd_id(case))
        if st in (200, 201, 409):
            CREATED["contractdefinitions"].append(cd_id(case))
    return cx_ok


def phase_visibility(dsp_bob, dsp_dave, cx_ok):
    log("== S2/S3 Sichtbarkeit und Katalogfilter")
    expected_bob = {asset_id(c) for c, v in CASES.items() if v["recipient"] == CONS_BPN
                    and (v["usage"] != "cx" or cx_ok)}
    expected_dave = {asset_id("neg")}

    t = time.time()
    st, cat_bob, ds_bob = catalog("consumer", dsp_bob, run_filters())
    seen_bob = {d.get("@id") for d in ds_bob}
    log(f"  Katalog Empfaenger->Anbieter (gefiltert): HTTP {st}, {len(ds_bob)} Datasets, {time.time() - t:.1f}s")
    st_d, _, ds_dave = catalog("negative", dsp_dave, run_filters())
    seen_dave = {d.get("@id") for d in ds_dave}
    log(f"  Katalog Gegenprobe->Anbieter (gefiltert): HTTP {st_d}, {len(ds_dave)} Datasets")

    if seen_bob == expected_bob and seen_dave == expected_dave:
        result("S2", "PASS", "Empfaenger sieht genau seine Zustellungen, Gegenprobe genau ihre",
               bob=sorted(seen_bob), dave=sorted(seen_dave))
    else:
        result("S2", "FAIL", "Sichtbarkeit weicht ab", bob=sorted(seen_bob), dave=sorted(seen_dave),
               expectedConsumer=sorted(expected_bob), expectedNegative=sorted(expected_dave))

    st_all, _, ds_all = catalog("consumer", dsp_bob, None)
    in_all = {d.get("@id") for d in ds_all if str(d.get("@id", "")).startswith(f"drive-spike-{RUN}-")}
    st_r, _, ds_r = catalog("consumer", dsp_bob, run_filters([(DRIVE + "recipientBpn", CONS_BPN)]))
    filter_ok = seen_bob == expected_bob and all(str(d.get("@id")).startswith(f"drive-spike-{RUN}-") for d in ds_bob)
    result("S3", "PASS" if filter_ok and {d.get('@id') for d in ds_r} == expected_bob else "FAIL",
           f"Filter auf drive:kind/spikeRun liefert {len(ds_bob)} von {len(ds_all)} Datasets; "
           f"Filter zusaetzlich auf recipientBpn liefert {len(ds_r)}",
           unfilteredCount=len(ds_all), unfilteredSpike=sorted(in_all))

    small_b = next((d for d in ds_bob if d.get("@id") == asset_id("smallb")), None)
    name = pick(small_b, "name") if small_b else None
    result("S13", "PASS" if name == CASES["smallb"]["title"] else "FAIL",
           f"Umlaute im Titel kommen im Katalog an: {name!r}")
    if small_b is not None:
        log("  Beispiel-Dataset (gekuerzt): " + json.dumps(small_b, ensure_ascii=False)[:900])
    if isinstance(cat_bob, dict):
        log("  Katalog-@context: " + json.dumps(cat_bob.get("@context"), ensure_ascii=False)[:600])
    return cat_bob, ds_bob


def phase_dave_negative(dsp_dave, cat_bob, ds_bob):
    log("== S2 Gegenprobe verhandelt mit dem Offer des Empfaengers")
    ds = next((d for d in ds_bob if d.get("@id") == asset_id("smallb")), None)
    if not ds:
        result("S2", "SKIP", "Dataset des Empfaengers fehlt, Gegenprobe nicht moeglich")
        return
    neg = negotiate("negative", dsp_dave, ds, cat_bob.get("@context"), asset_id("smallb"))
    if neg["ok"]:
        result("S2", "FAIL", "Gegenprobe konnte mit fremdem Offer einen Vertrag schliessen", attempts=neg["attempts"])
    else:
        result("S2", "PASS", "Verhandlung der Gegenprobe mit fremdem Offer scheitert", attempts=neg["attempts"])


def phase_negotiations(dsp_bob, cx_ok):
    log("== S1/S5 Verhandlung und EDR fuer beide Usage-Profile")
    out = {}
    for case in ("smalla", "smallb"):
        if case == "smalla" and not cx_ok:
            continue
        t = time.time()
        neg, edr = accept(case, dsp_bob)
        out[case] = (neg, edr)
        if case == "smalla":
            if neg["ok"]:
                result("S1", "PASS", "CX-Profil verhandelbar (FINALIZED)", attempts=neg["attempts"])
            else:
                result("S1", "FAIL", "CX-Profil nicht verhandelbar", attempts=neg["attempts"])
            continue
        if not neg["ok"] or not edr:
            result("S5", "FAIL", "Membership-Profil: kein Vertrag oder kein EDR", attempts=neg["attempts"])
            continue
        dl = download(edr["endpoint"], edr["authorization"])
        ok = dl["status"] == 200 and dl["sha256"] == expected_sha(0, CASES[case]["size"])
        result("S5", "PASS" if ok else "FAIL",
               f"Katalog->FINALIZED {neg['seconds']}s (Variante {neg['variant']}), FINALIZED->EDR {edr['seconds']}s, "
               f"gesamt {time.time() - t:.1f}s; Download HTTP {dl['status']}, Hash {'ok' if ok else 'FALSCH'}",
               download=dl, attempts=neg["attempts"], endpoint=edr["endpoint"])
    return out


def phase_headers(neg_b, edr_b):
    log("== S4 Header, Consumer-Header und Pfade am Backend")
    if not edr_b:
        result("S4", "SKIP", "kein EDR fuer smallb")
        return
    mark = source_seq()
    download(edr_b["endpoint"], edr_b["authorization"])
    entries = [e for e in source_log(mark) if e.get("deliveryId") == delivery_id("smallb")]
    if not entries:
        result("S4", "FAIL", "Quelle hat keinen Abruf protokolliert")
        return
    hdr = {k.lower(): v for k, v in entries[-1]["headers"].items()}
    edc_headers = {k: v for k, v in hdr.items() if k.startswith("edc-")}
    # Der Header traegt die Agreement-ID des Anbieters, nicht die contractAgreementId der Verhandlung beim
    # Empfaenger. Der Content-Endpunkt schlaegt sie im eigenen EDC nach und vergleicht das Asset.
    agreement_header = hdr.get("edc-contract-agreement-id", "-")
    ast, agr = mgmt("provider", "GET", f"/v3/contractagreements/{urllib.parse.quote(agreement_header)}")
    agreement_ok = ast == 200 and pick(agr, "assetId") == asset_id("smallb")
    ok = entries[-1].get("tokenValid") and hdr.get("edc-bpn") == CONS_BPN and agreement_ok
    result("S4", "PASS" if ok else "FAIL",
           f"Token gueltig={entries[-1].get('tokenValid')}, Edc-Bpn={hdr.get('edc-bpn')}, "
           f"Agreement-Header passt={agreement_ok}", edcHeaders=edc_headers, allHeaders=sorted(hdr))

    mark = source_seq()
    download(edr_b["endpoint"], edr_b["authorization"], {"Edc-Bpn": NEG_BPN, "X-Spike-Probe": "1"})
    e = (source_log(mark) or [{}])[-1]
    hdr = {k.lower(): v for k, v in e.get("headers", {}).items()}
    spoof_ok = hdr.get("edc-bpn") == CONS_BPN and "x-spike-probe" not in hdr
    result("S4", "PASS" if spoof_ok else "FAIL",
           f"Vom Consumer gesetzte Header: Edc-Bpn an der Quelle={hdr.get('edc-bpn')}, "
           f"X-Spike-Probe durchgereicht={'x-spike-probe' in hdr}")

    probes = {
        "items": "/items/1",
        "dotdot-encoded": f"/items/..%2F..%2F{delivery_id('large')}",
        "dotdot-plain": f"/../{delivery_id('large')}",
    }
    seen = {}
    for name, suffix in probes.items():
        mark = source_seq()
        dl = download(edr_b["endpoint"].rstrip("/") + suffix, edr_b["authorization"], timeout=30)
        e = (source_log(mark) or [{}])[-1]
        seen[name] = {"consumerStatus": dl["status"], "bytes": dl["bytes"], "sourcePath": e.get("rawPath"),
                      "sourceNormalized": e.get("normalizedPath"), "sourceStatus": e.get("status")}
    items_ok = seen["items"]["sourceNormalized"] == f"/edc/deliveries/{delivery_id('smallb')}/items/1"
    no_cross = all(v["bytes"] == 0 or v["sourceNormalized"] in (None, f"/edc/deliveries/{delivery_id('smallb')}")
                   for k, v in seen.items() if k != "items")
    result("S4", "PASS" if items_ok and no_cross else "FAIL",
           "proxyPath haengt Unterpfade an; Traversal fuehrt nicht zu einer fremden Zustellung", probes=seen)


def phase_large(dsp_bob):
    log(f"== S6 Grosse Datei ({LARGE_MB} MiB)")
    neg, edr = accept("large", dsp_bob)
    if not edr:
        result("S6", "FAIL", "kein EDR fuer large", attempts=neg.get("attempts"))
        return
    dl = download(edr["endpoint"], edr["authorization"], timeout=120)
    ok = dl["status"] == 200 and dl["sha256"] == expected_sha(0, CASES["large"]["size"])
    result("S6", "PASS" if ok else "FAIL",
           f"{LARGE_MB} MiB in {dl['seconds']}s ({dl['mbps']} MiB/s), TTFB {dl['ttfb']}s, Hash {'ok' if ok else 'FALSCH'}",
           download=dl)


def phase_parallel(dsp_bob):
    log(f"== S6 {PAR_COUNT} parallele Downloads a {PAR_MB} MiB ueber ein EDR")
    neg, edr = accept("par", dsp_bob)
    if not edr:
        result("S6", "FAIL", "kein EDR fuer par", attempts=neg.get("attempts"))
        return
    mark = source_seq()
    with concurrent.futures.ThreadPoolExecutor(max_workers=PAR_COUNT) as pool:
        futures = [pool.submit(download, edr["endpoint"], edr["authorization"], None, 180) for _ in range(PAR_COUNT)]
        results = [f.result() for f in futures]
    want = expected_sha(0, CASES["par"]["size"])
    good = sum(1 for r in results if r["status"] == 200 and r["sha256"] == want)
    entries = [e for e in source_log(mark) if e.get("deliveryId") == delivery_id("par")]
    max_conc = max((e.get("activeAtStart", 0) + 1 for e in entries), default=0)
    ttfbs = sorted(r["ttfb"] for r in results if r["ttfb"] is not None)
    result("S6", "PASS" if good == PAR_COUNT else "FAIL",
           f"{good}/{PAR_COUNT} vollstaendig; max. gleichzeitige Streams an der Quelle {max_conc}; "
           f"TTFB min/max {ttfbs[0] if ttfbs else '-'}s/{ttfbs[-1] if ttfbs else '-'}s; "
           f"Dauer min/max {min(r['seconds'] for r in results)}s/{max(r['seconds'] for r in results)}s",
           downloads=results, sourceStarts=[round(e['ts'] - T0, 2) for e in entries], maxConcurrent=max_conc)


def phase_segments(dsp_bob):
    log("== S6 Segmentierter Abruf ueber proxyQueryParams (offset/length)")
    neg, edr = accept("seg", dsp_bob)
    if not edr:
        result("S6", "FAIL", "kein EDR fuer seg", attempts=neg.get("attempts"))
        return
    size, seg = CASES["seg"]["size"], 8 * MIB
    h, parts = hashlib.sha256(), []
    for off in range(0, size, seg):
        url = f"{edr['endpoint']}?offset={off}&length={seg}"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Authorization", edr["authorization"])
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            parts.append({"offset": off, "status": 200, "bytes": len(data)})
            h.update(data)
        except Exception as e:  # noqa: BLE001
            parts.append({"offset": off, "error": repr(e)[:200]})
    ok = h.hexdigest() == expected_sha(0, size)
    result("S6", "PASS" if ok else "FAIL", f"{len(parts)} Segmente a 8 MiB, zusammengesetzt Hash {'ok' if ok else 'FALSCH'}",
           parts=parts)


def phase_revocation(dsp_bob):
    log("== S7/S9 Widerruf waehrend eines laufenden Downloads")
    st, cat, ds = catalog("consumer", dsp_bob, run_filters([(EDC + "id", asset_id("slow"))]))
    old = next((d for d in ds if d.get("@id") == asset_id("slow")), None)
    neg, edr = accept("slow", dsp_bob)
    if not edr:
        result("S7", "FAIL", "kein EDR fuer slow", attempts=neg.get("attempts"))
        return
    holder = {}
    worker = threading.Thread(target=lambda: holder.update(dl=download(edr["endpoint"], edr["authorization"],
                                                                       timeout=60)))
    worker.start()
    time.sleep(10)

    query = {"@context": {"@vocab": EDC}, "@type": "QuerySpec", "limit": 50, "filterExpression": [
        {"operandLeft": "assetId", "operator": "=", "operandRight": asset_id("slow")}]}
    st, agreements = mgmt("provider", "POST", "/v3/contractagreements/request", query)
    ids = [a.get("@id") for a in as_list(agreements) if isinstance(a, dict)]
    retired_at = time.time()
    for aid in ids:
        rst, rr = mgmt("provider", "POST", "/v3/contractagreements/retirements", {
            "@context": {"@vocab": EDC, "tx": TX}, "@type": "AgreementsRetirementEntry",
            "agreementId": aid, "tx:reason": "Spike: Freigabe widerrufen"})
        log(f"  Retirement {aid}: HTTP {rst} {'' if rst in (200, 204) else str(rr)[:200]}")
    worker.join(timeout=240)
    dl = holder.get("dl", {})
    full = CASES["slow"]["size"]
    cut = dl.get("bytes", 0) < full
    result("S7", "INFO",
           f"Laufender Download nach Retirement {'abgebrochen' if cut else 'NICHT abgebrochen'}: "
           f"{dl.get('bytes', 0) // MIB}/{full // MIB} MiB, Ende {round(dl.get('end', 0) - (retired_at - T0), 1)}s "
           f"nach dem Retirement", download=dl, agreementQueryStatus=st, agreements=ids)

    time.sleep(5)
    st_tp, tp = mgmt("consumer", "GET", f"/v3/transferprocesses/{urllib.parse.quote(edr['transferProcessId'])}")
    again = download(edr["endpoint"], edr["authorization"], timeout=30)
    result("S7", "PASS" if again["status"] != 200 or again["bytes"] == 0 else "FAIL",
           f"Abruf mit altem Token nach Retirement: HTTP {again['status']}, {again['bytes']} Bytes; "
           f"Transferstatus beim Empfaenger: {pick(tp, 'state')}")

    dst, _ = mgmt("provider", "DELETE", f"/v3/contractdefinitions/{cd_id('slow')}")
    if dst in (200, 204):
        CREATED["contractdefinitions"].remove(cd_id("slow"))
    _, _, ds_after = catalog("consumer", dsp_bob, run_filters([(EDC + "id", asset_id("slow"))]))
    gone = not any(d.get("@id") == asset_id("slow") for d in ds_after)
    renegotiation = negotiate("consumer", dsp_bob, old, cat.get("@context") if isinstance(cat, dict) else None,
                              asset_id("slow"), expected_permissions("slow")) if old else {"ok": None, "attempts": []}
    result("S7", "PASS" if gone and not renegotiation["ok"] else "FAIL",
           f"Contract Definition geloescht (HTTP {dst}): aus dem Katalog des Empfaengers verschwunden={gone}; "
           f"Neuverhandlung mit altem Offer erfolgreich={renegotiation['ok']}", attempts=renegotiation["attempts"])

    ast, ar = mgmt("provider", "DELETE", f"/v3/assets/{asset_id('slow')}")
    result("S9", "PASS" if ast == 409 else "INFO",
           f"Asset mit Agreements loeschen: HTTP {ast} {'' if ast == 409 else str(ar)[:200]}")
    if ast in (200, 204):
        CREATED["assets"].remove(asset_id("slow"))


def phase_cleanup():
    log("== Aufraeumen")
    for tp in CONSUMER_TPS:
        mgmt("consumer", "POST", f"/v3/transferprocesses/{urllib.parse.quote(tp)}/terminate",
             {"@context": {"@vocab": EDC}, "@type": "TerminateTransfer", "reason": "spike finished"})
    for kind in ("contractdefinitions", "policydefinitions", "assets"):
        for oid in list(CREATED[kind]):
            st, _ = mgmt("provider", "DELETE", f"/v3/{kind}/{urllib.parse.quote(oid)}")
            log(f"  DELETE {kind}/{oid} -> {st}")


def main():
    log(f"DataspaceDrive Spike, Lauf {RUN}: Anbieter {PROV_BPN}, Empfaenger {CONS_BPN}, Gegenprobe {NEG_BPN}")
    phase_preflight()
    phase_s8_network_policy()

    dsp_bob, info_bob = discover("consumer")
    dsp_dave, info_dave = discover("negative")
    result("S5", "INFO", f"Discovery Empfaenger->Anbieter: {dsp_bob['protocol']} / {dsp_bob['counterPartyId']}",
           bob=info_bob, dave=info_dave)
    STATE["contract_variant"] = phase_policy_probe(dsp_bob) or "and"
    cx_ok = phase_provider_setup(STATE["contract_variant"])

    cat_bob, ds_bob = phase_visibility(dsp_bob, dsp_dave, cx_ok)
    phase_dave_negative(dsp_dave, cat_bob, ds_bob)
    negs = phase_negotiations(dsp_bob, cx_ok)
    neg_b, edr_b = negs.get("smallb", ({}, None))
    phase_headers(neg_b, edr_b)
    phase_large(dsp_bob)
    phase_parallel(dsp_bob)
    phase_segments(dsp_bob)
    phase_revocation(dsp_bob)
    if CLEANUP:
        phase_cleanup()

    log("== Ergebnis")
    for sid in sorted(RESULTS, key=lambda s: int(s[1:])):
        for r in RESULTS[sid]:
            log(f"  {sid:4} {r['status']:5} {r['summary']}")
    print("=====SPIKE-RESULT-JSON-BEGIN=====")
    print(json.dumps({"run": RUN, "seconds": round(time.time() - T0, 1), "results": RESULTS}, ensure_ascii=False))
    print("=====SPIKE-RESULT-JSON-END=====", flush=True)


if __name__ == "__main__":
    main()
