#!/usr/bin/env python3
"""從 Codex app-server 的 JSON schema 產生 C# 協定型別。

對應 Python SDK 的 scripts/update_sdk_artifacts.py：Python 版用 datamodel-code-generator 產生 pydantic 模型，
這裡自己走訪 schema 產生 C# record，以及 System.Text.Json 的 source-gen context（維持 AOT 相容）。

用法：
    python tools/generate_protocol.py                        # 用 schema/ 裡已提交的 schema 重新產生
    python tools/generate_protocol.py --from-codex <codex>   # 先用指定的 codex 產生 schema 並更新 schema/，再產生程式碼

只產生 SDK 用得到的型別：所有 server 通知與 server 請求，加上 CLIENT_METHODS 列出的 client 方法，以及它們參考到的型別。
要包裝新的 client 方法時，把它加進 CLIENT_METHODS 再重新產生。
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCHEMA_DIR = ROOT / "schema"
SCHEMA_FILE = SCHEMA_DIR / "codex_app_server_protocol.schemas.json"
VERSION_FILE = SCHEMA_DIR / "codex-version.txt"
OUT_DIR = ROOT / "src" / "PamisWang.CodexAgentSdk" / "Protocol" / "Generated"
NAMESPACE = "PamisWang.CodexAgentSdk.Protocol"

# SDK 包裝的 client → server 方法：(方法, params 型別, response 型別)
CLIENT_METHODS = [
    ("initialize", "InitializeParams", "InitializeResponse"),
    ("thread/start", "ThreadStartParams", "ThreadStartResponse"),
    ("thread/resume", "ThreadResumeParams", "ThreadResumeResponse"),
    ("thread/fork", "ThreadForkParams", "ThreadForkResponse"),
    ("thread/list", "ThreadListParams", "ThreadListResponse"),
    ("thread/read", "ThreadReadParams", "ThreadReadResponse"),
    ("thread/archive", "ThreadArchiveParams", "ThreadArchiveResponse"),
    ("thread/unarchive", "ThreadUnarchiveParams", "ThreadUnarchiveResponse"),
    ("thread/name/set", "ThreadSetNameParams", "ThreadSetNameResponse"),
    ("thread/compact/start", "ThreadCompactStartParams", "ThreadCompactStartResponse"),
    ("turn/start", "TurnStartParams", "TurnStartResponse"),
    ("turn/steer", "TurnSteerParams", "TurnSteerResponse"),
    ("turn/interrupt", "TurnInterruptParams", "TurnInterruptResponse"),
    ("model/list", "ModelListParams", "ModelListResponse"),
    ("account/read", "GetAccountParams", "GetAccountResponse"),
    ("account/login/start", "LoginAccountParams", "LoginAccountResponse"),
    ("account/login/cancel", "CancelLoginAccountParams", "CancelLoginAccountResponse"),
    ("account/logout", None, "LogoutAccountResponse"),
]

# 這些是 JSON-RPC 外層或方法清單，不產生型別；方法對應另外產生成 registry
SKIP_DEFINITIONS = {
    "ClientRequest", "ClientNotification", "ServerRequest", "ServerNotification",
    "JSONRPCMessage", "JSONRPCRequest", "JSONRPCNotification", "JSONRPCResponse",
    "JSONRPCError", "JSONRPCErrorError",
}

# record 自動產生的成員；屬性撞名時改名
RESERVED_MEMBERS = {"EqualityContract", "Equals", "GetHashCode", "ToString", "PrintMembers", "Deconstruct", "GetType"}


# ---------------------------------------------------------------- 名稱

def pascal(text: str) -> str:
    """agentMessage → AgentMessage、on-request → OnRequest、mcp_elicitations → McpElicitations。"""
    parts = [p for p in re.split(r"[^0-9A-Za-z]+", text) if p]
    out = []
    for part in parts:
        if part.isupper() and len(part) > 1:
            part = part.lower()
        out.append(part[0].upper() + part[1:])
    name = "".join(out) or "Empty"
    if name[0].isdigit():
        name = "V" + name
    return name


def clean_title(title: str) -> str:
    """schema 的 title 可能帶 Rust 的模組路徑，例如 ApiKeyv2::LoginAccountParams；去掉 v2:: 這段。"""
    return re.sub(r"v\d+::", "", title)


def xml_escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def doc_lines(description: str | None, indent: str) -> list[str]:
    if not description:
        return []
    lines = [line.rstrip() for line in xml_escape(description.strip()).splitlines()]
    out = [f"{indent}/// <summary>"]
    out += [f"{indent}/// {line}".rstrip() for line in lines]
    out.append(f"{indent}/// </summary>")
    return out


def cs_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


# ---------------------------------------------------------------- schema 輔助

def load_definitions(schema: dict) -> dict:
    """bundle 的 definitions 分兩層：最外層是 v1 與 JSON-RPC 型別，definitions.v2 是 v2 型別。攤平成一層。"""
    defs = dict(schema["definitions"].get("v2", {}))
    for name, value in schema["definitions"].items():
        if name != "v2":
            defs[name] = value
    return defs


def ref_name(ref: str) -> str:
    return ref.rsplit("/", 1)[-1]


def is_null_schema(s) -> bool:
    return isinstance(s, dict) and s.get("type") == "null" and len(set(s) - {"type", "description"}) == 0


def is_string_enum_variant(s) -> bool:
    return (isinstance(s, dict) and "enum" in s and s.get("type") in (None, "string")
            and all(isinstance(v, str) for v in s["enum"]) and "properties" not in s)


def is_object_variant(s) -> bool:
    return isinstance(s, dict) and ("properties" in s) and s.get("type") in (None, "object")


def find_discriminator(variants: list) -> str | None:
    common = None
    for v in variants:
        props = v.get("properties", {})
        names = {k for k, p in props.items()
                 if isinstance(p, dict) and isinstance(p.get("enum"), list) and len(p["enum"]) == 1
                 and isinstance(p["enum"][0], str)}
        common = names if common is None else common & names
    if not common:
        return None
    # 優先用慣例的名稱
    for preferred in ("type", "method", "kind", "handlerType"):
        if preferred in common:
            return preferred
    return sorted(common)[0]


def is_any(s) -> bool:
    return s is True or s == {} or (isinstance(s, dict) and set(s) <= {"description", "default", "title", "$schema"})


# ---------------------------------------------------------------- 型別宣告

@dataclass
class Prop:
    json_name: str
    cs_name: str
    cs_type: str
    nullable: bool
    required: bool
    description: str | None
    const: str | None = None


@dataclass
class RecordDecl:
    name: str
    description: str | None
    props: list[Prop] = field(default_factory=list)
    base: str | None = None
    discriminator: tuple[str, str, str] | None = None  # (json 名稱, C# 名稱, 值)


@dataclass
class EnumDecl:
    name: str
    description: str | None
    values: list[tuple[str, str, str | None]] = field(default_factory=list)  # (值, C# 名稱, 說明)


@dataclass
class TaggedDecl:
    name: str
    description: str | None
    disc_json: str
    disc_cs: str
    base_props: list[Prop] = field(default_factory=list)
    variants: list[tuple[str, str]] = field(default_factory=list)  # (值, C# 型別)


@dataclass
class UnionDecl:
    name: str
    description: str | None
    strings: list[tuple[str, str, str | None]] = field(default_factory=list)
    free_string: bool = False
    integer: bool = False
    others: list[str] = field(default_factory=list)  # 物件或陣列變體的 C# 型別


class Generator:
    def __init__(self, defs: dict):
        self.defs = defs
        self.decls: dict[str, object] = {}
        self.def_types: dict[str, str] = {}
        self.inline_types: dict[str, str] = {}
        self.order: list[str] = []
        self._nullable_guard: set[str] = set()

    # -------- 名稱分配

    def reserve(self, wanted: str, schema) -> str:
        """分配不重複的 C# 型別名稱。schema 一樣的 inline 型別共用同一個名稱。"""
        key = json.dumps(schema, sort_keys=True)
        if key in self.inline_types:
            return self.inline_types[key]
        name = wanted
        counter = 2
        # inline 型別也避開 definitions 的名稱：那個 definition 之後可能也會產生
        while name in self.decls or name in self.defs:
            name = f"{wanted}{counter}"
            counter += 1
        self.inline_types[key] = name
        return name

    def register(self, decl) -> None:
        self.decls[decl.name] = decl
        self.order.append(decl.name)

    # -------- 解析

    def ref(self, ref: str) -> str:
        return self.definition(ref_name(ref))

    def definition(self, name: str) -> str:
        if name in self.def_types:
            return self.def_types[name]
        schema = self.defs[name]
        result = self.build(name, schema, is_definition=True)
        self.def_types[name] = result
        return result

    def is_nullable(self, s) -> bool:
        if is_any(s):
            return True
        if not isinstance(s, dict):
            return False
        if "$ref" in s:
            target = ref_name(s["$ref"])
            if target in self._nullable_guard:
                return False
            self._nullable_guard.add(target)
            try:
                return self.is_nullable(self.defs[target])
            finally:
                self._nullable_guard.discard(target)
        if "allOf" in s and len(s["allOf"]) == 1:
            return self.is_nullable(s["allOf"][0])
        t = s.get("type")
        if t == "null" or (isinstance(t, list) and "null" in t):
            return True
        for key in ("anyOf", "oneOf"):
            if key in s and any(is_null_schema(v) for v in s[key]):
                return True
        if isinstance(s.get("enum"), list) and None in s["enum"]:
            return True
        return False

    def build(self, name: str, s, is_definition: bool = False) -> str:
        """回傳 schema 對應的 C# 型別（不含可為 null 的 ?）；需要新型別時順便宣告。"""
        if is_any(s):
            return "JsonNode"
        if "$ref" in s:
            return self.ref(s["$ref"])
        if "allOf" in s and len(s["allOf"]) == 1 and "properties" not in s:
            return self.build(name, s["allOf"][0])

        title = s.get("title")
        type_name = name if is_definition else pascal(clean_title(title)) if isinstance(title, str) else name

        for key in ("oneOf", "anyOf"):
            if key in s and "properties" not in s:
                variants = s[key]
                non_null = [v for v in variants if not is_null_schema(v)]
                if len(non_null) == 1:
                    return self.build(name, non_null[0])
                if all(is_string_enum_variant(v) for v in non_null):
                    values = []
                    for v in non_null:
                        for value in v["enum"]:
                            values.append((value, v.get("description")))
                    return self.enum(type_name, s, values, is_definition)
                if all(is_object_variant(v) for v in non_null) and (disc := find_discriminator(non_null)):
                    return self.tagged(type_name, s, non_null, disc, is_definition)
                return self.union(type_name, s, non_null, is_definition)
            if key in s and "properties" in s:
                variants = [v for v in s[key] if not is_null_schema(v)]
                if all(is_object_variant(v) for v in variants) and (disc := find_discriminator(variants)):
                    return self.tagged(type_name, s, variants, disc, is_definition)

        if isinstance(s.get("enum"), list):
            values = [v for v in s["enum"] if v is not None]
            if values and all(isinstance(v, str) for v in values):
                return self.enum(type_name, s, [(v, None) for v in values], is_definition)
            return "JsonNode"

        t = s.get("type")
        types = [x for x in (t if isinstance(t, list) else [t]) if x != "null"]
        if len(types) > 1:
            return "JsonNode"
        t = types[0] if types else None

        if t == "object" or "properties" in s:
            extra = s.get("additionalProperties")
            # 有名字的定義即使沒有欄位（例如 ThreadArchiveResponse）也產生 record，API 才有明確的型別
            if s.get("properties") or (is_definition and not (isinstance(extra, dict) and extra)):
                return self.record(type_name, s, is_definition=is_definition)
            if isinstance(extra, dict) and extra:
                value = self.build(name + "Value", extra)
                return f"IReadOnlyDictionary<string, {value}{'?' if self.is_nullable(extra) else ''}>"
            return "JsonObject"
        if t == "array":
            items = s.get("items")
            if not isinstance(items, dict) or not items:
                return "IReadOnlyList<JsonNode?>"
            inner = self.build(name + "Item", items)
            return f"IReadOnlyList<{inner}{'?' if self.is_nullable(items) else ''}>"
        if t == "string":
            return "string"
        if t == "integer":
            return "int" if s.get("format") in ("int8", "int16", "int32", "uint8", "uint16") else "long"
        if t == "number":
            return "double"
        if t == "boolean":
            return "bool"
        return "JsonNode"

    def props_of(self, owner: str, s: dict, skip: set[str]) -> list[Prop]:
        required = set(s.get("required", []))
        properties = dict(s.get("properties") or {})
        # 變體裡又有 anyOf/oneOf（例如圖片輸入是 url 或 fileId 二選一）：子變體的欄位都當成選填
        for key in ("anyOf", "oneOf"):
            for sub in s.get(key) or []:
                if isinstance(sub, dict):
                    for pname, pschema in (sub.get("properties") or {}).items():
                        properties.setdefault(pname, pschema)
        used = set()
        props = []
        for json_name, ps in properties.items():
            if json_name in skip:
                continue
            cs_name = pascal(json_name)
            if cs_name == owner or cs_name in RESERVED_MEMBERS:
                cs_name += "Value"
            while cs_name in used:
                cs_name += "_"
            used.add(cs_name)
            description = ps.get("description") if isinstance(ps, dict) else None
            if isinstance(ps, dict) and isinstance(ps.get("enum"), list) and len(ps["enum"]) == 1 \
                    and isinstance(ps["enum"][0], str):
                props.append(Prop(json_name, cs_name, "string", False, False, description, const=ps["enum"][0]))
                continue
            cs_type = self.build(owner + pascal(json_name), ps)
            is_required = json_name in required
            nullable = self.is_nullable(ps) or not is_required or cs_type in ("JsonNode",)
            props.append(Prop(json_name, cs_name, cs_type, nullable, is_required, description))
        return props

    def record(self, wanted: str, s: dict, base: str | None = None,
               discriminator: tuple[str, str, str] | None = None, skip: set[str] | None = None,
               is_definition: bool = False) -> str:
        name = wanted if is_definition else self.reserve(wanted, s)
        if name in self.decls:
            return name
        decl = RecordDecl(name, s.get("description"), base=base, discriminator=discriminator)
        self.register(decl)
        if is_definition:
            self.def_types[name] = name
        decl.props = self.props_of(name, s, skip or set())
        return name

    def enum(self, wanted: str, s: dict, values: list, is_definition: bool) -> str:
        name = wanted if is_definition else self.reserve(wanted, s)
        if name in self.decls:
            return name
        decl = EnumDecl(name, s.get("description"))
        self.register(decl)
        used = {"Value", name} | RESERVED_MEMBERS
        for value, description in values:
            cs_name = pascal(value)
            while cs_name in used:
                cs_name += "Value"
            used.add(cs_name)
            decl.values.append((value, cs_name, description))
        return name

    def tagged(self, wanted: str, s: dict, variants: list, disc: str, is_definition: bool) -> str:
        name = wanted if is_definition else self.reserve(wanted, s)
        if name in self.decls:
            return name
        decl = TaggedDecl(name, s.get("description"), disc, pascal(disc))
        self.register(decl)
        if is_definition:
            self.def_types[name] = name
        common = {k: v for k, v in (s.get("properties") or {}).items() if k != disc}
        if common:
            decl.base_props = self.props_of(name, {"properties": common, "required": []}, set())
            for prop in decl.base_props:
                # 基底的欄位一律選填：不認得的變體也要能建立
                prop.required = False
                prop.nullable = True
        skip = {disc} | set(common)
        for v in variants:
            value = v["properties"][disc]["enum"][0]
            title = v.get("title")
            variant_name = pascal(clean_title(title)) if isinstance(title, str) else pascal(value) + name
            variant = self.record(variant_name, v, base=name, discriminator=(disc, pascal(disc), value), skip=skip)
            decl.variants.append((value, variant))
        return name

    def union(self, wanted: str, s: dict, variants: list, is_definition: bool) -> str:
        name = wanted if is_definition else self.reserve(wanted, s)
        if name in self.decls:
            return name
        decl = UnionDecl(name, s.get("description"))
        self.register(decl)
        if is_definition:
            self.def_types[name] = name
        used = {name, "Raw", "StringValue", "Int64Value", "From", "FromJson", "FromString", "FromInt64"} | RESERVED_MEMBERS
        for index, v in enumerate(variants):
            if is_string_enum_variant(v):
                for value in v["enum"]:
                    cs_name = pascal(value)
                    while cs_name in used:
                        cs_name += "Value"
                    used.add(cs_name)
                    decl.strings.append((value, cs_name, v.get("description")))
                continue
            vt = v.get("type") if isinstance(v, dict) else None
            if vt == "string":
                decl.free_string = True
                continue
            if vt == "integer":
                decl.integer = True
                continue
            title = v.get("title") if isinstance(v, dict) else None
            variant_name = pascal(clean_title(title)) if isinstance(title, str) else f"{name}Variant{index + 1}"
            decl.others.append(self.build(variant_name, v))
        return name


# ---------------------------------------------------------------- 輸出

HEADER = """// <auto-generated>
//     由 tools/generate_protocol.py 從 schema/codex_app_server_protocol.schemas.json 產生（Codex CLI {version}），不要手動修改。
//     Generated by tools/generate_protocol.py from the schema of Codex CLI {version}. Do not edit.
// </auto-generated>

#nullable enable
#pragma warning disable CS1591

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace {namespace};
"""


def type_ref(prop: Prop) -> str:
    return prop.cs_type + ("?" if prop.nullable else "")


def emit_prop(prop: Prop, indent: str = "    ") -> list[str]:
    out = doc_lines(prop.description, indent)
    out.append(f'{indent}[JsonPropertyName({cs_string(prop.json_name)})]')
    if prop.const is not None:
        out.append(f"{indent}public string {prop.cs_name} {{ get; init; }} = {cs_string(prop.const)};")
        return out
    required = "required " if prop.required else ""
    out.append(f"{indent}public {required}{type_ref(prop)} {prop.cs_name} {{ get; init; }}")
    return out


def emit_record(decl: RecordDecl) -> list[str]:
    out = doc_lines(decl.description, "")
    inherit = f" : {decl.base}" if decl.base else ""
    out.append(f"public sealed record {decl.name}{inherit}")
    out.append("{")
    members = []
    if decl.discriminator:
        json_name, cs_name, value = decl.discriminator
        # 不讓 System.Text.Json 處理：它的 source generator 遇到 abstract + override 的同名屬性會產生重複的區域變數。
        # 辨識欄位由 union 的 converter 寫出、讀入
        members.append([
            "    /// <inheritdoc/>",
            "    [JsonIgnore]",
            f"    public override string {cs_name} => {cs_string(value)};",
        ])
    for prop in decl.props:
        members.append(emit_prop(prop))
    for index, member in enumerate(members):
        if index:
            out.append("")
        out += member
    out.append("}")
    return out


def emit_enum(decl: EnumDecl) -> list[str]:
    out = doc_lines(decl.description, "")
    out.append(f"[JsonConverter(typeof(StringValueConverter<{decl.name}>))]")
    out.append(f"public readonly record struct {decl.name}(string Value) : IStringValue<{decl.name}>")
    out.append("{")
    for value, cs_name, description in decl.values:
        out += doc_lines(description, "    ")
        out.append(f"    public static {decl.name} {cs_name} {{ get; }} = new({cs_string(value)});")
        out.append("")
    out.append(f"    static {decl.name} IStringValue<{decl.name}>.FromValue(string value) => new(value);")
    out.append("")
    out.append("    /// <inheritdoc/>")
    out.append("    public override string ToString() => Value;")
    out.append("}")
    return out


def emit_tagged(decl: TaggedDecl) -> list[str]:
    name = decl.name
    # 協定本身可能就有值叫 unknown 的變體（例如 UnknownCommandAction），這時改用 Unrecognized
    fallback = f"Unknown{name}" if f"Unknown{name}" not in {v for _, v in decl.variants} else f"Unrecognized{name}"
    out = doc_lines(decl.description, "")
    out.append(f"[JsonConverter(typeof({name}JsonConverter))]")
    out.append(f"public abstract record {name}")
    out.append("{")
    out.append(f"    /// <summary>辨識變體的 <c>{decl.disc_json}</c> 欄位。</summary>")
    out.append("    [JsonIgnore]")
    out.append(f"    public abstract string {decl.disc_cs} {{ get; }}")
    for prop in decl.base_props:
        out.append("")
        out += emit_prop(prop)
    out.append("}")
    out.append("")
    out.append(f"/// <summary>這個 SDK 不認得的 <see cref=\"{name}\"/> 變體；原始 JSON 在 <see cref=\"Raw\"/>。</summary>")
    out.append(f"public sealed record {fallback} : {name}")
    out.append("{")
    out.append(f"    internal {fallback}(string {decl.disc_cs.lower()}, JsonElement raw)")
    out.append("    {")
    out.append(f"        {decl.disc_cs} = {decl.disc_cs.lower()};")
    out.append("        Raw = raw;")
    out.append("    }")
    out.append("")
    out.append("    /// <inheritdoc/>")
    out.append("    [JsonIgnore]")
    out.append(f"    public override string {decl.disc_cs} {{ get; }}")
    out.append("")
    out.append("    /// <summary>原始 JSON。</summary>")
    out.append("    [JsonIgnore]")
    out.append("    public JsonElement Raw { get; }")
    out.append("}")
    out.append("")
    out.append(f"internal sealed class {name}JsonConverter : JsonConverter<{name}>")
    out.append("{")
    out.append(f"    public override {name}? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)")
    out.append("    {")
    out.append("        using var document = JsonDocument.ParseValue(ref reader);")
    out.append("        var root = document.RootElement;")
    out.append(f"        var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty({cs_string(decl.disc_json)}, out var t)")
    out.append("            && t.ValueKind == JsonValueKind.String ? t.GetString() ?? \"\" : \"\";")
    out.append("        try")
    out.append("        {")
    out.append("            return tag switch")
    out.append("            {")
    for value, variant in decl.variants:
        out.append(f"                {cs_string(value)} => root.Deserialize(ProtocolJsonContext.Default.{variant}),")
    out.append(f"                _ => new {fallback}(tag, root.Clone()),")
    out.append("            };")
    out.append("        }")
    out.append("        catch (JsonException)")
    out.append("        {")
    out.append("            // 認得的變體卻缺欄位或型別不符：多半是協定改版，當成不認得的變體保留原始 JSON")
    out.append(f"            return new {fallback}(tag, root.Clone());")
    out.append("        }")
    out.append("    }")
    out.append("")
    out.append(f"    public override void Write(Utf8JsonWriter writer, {name} value, JsonSerializerOptions options)")
    out.append("    {")
    out.append("        switch (value)")
    out.append("        {")
    for value, variant in decl.variants:
        out.append(f"            case {variant} v:")
        out.append(f"                WriteVariant(writer, value.{decl.disc_cs}, JsonSerializer.SerializeToElement(v, ProtocolJsonContext.Default.{variant}));")
        out.append("                break;")
    out.append(f"            case {fallback} v:")
    out.append("                v.Raw.WriteTo(writer);")
    out.append("                break;")
    out.append("            default:")
    out.append(f"                throw new JsonException($\"無法序列化 {{value.GetType()}}。\");")
    out.append("        }")
    out.append("    }")
    out.append("")
    out.append("    // 辨識欄位放第一個，後面接變體自己的欄位")
    out.append("    private static void WriteVariant(Utf8JsonWriter writer, string tag, JsonElement properties)")
    out.append("    {")
    out.append("        writer.WriteStartObject();")
    out.append(f"        writer.WriteString({cs_string(decl.disc_json)}, tag);")
    out.append("        foreach (var property in properties.EnumerateObject())")
    out.append("        {")
    out.append("            property.WriteTo(writer);")
    out.append("        }")
    out.append("")
    out.append("        writer.WriteEndObject();")
    out.append("    }")
    out.append("}")
    return out


def union_method_suffix(cs_type: str) -> str:
    m = re.fullmatch(r"IReadOnlyList<(\w+)\??>", cs_type)
    if m:
        return pascal(m.group(1)) + "List"
    m = re.fullmatch(r"IReadOnlyDictionary<string, (\w+)\??>", cs_type)
    if m:
        return pascal(m.group(1)) + "Map"
    return pascal(cs_type)


def emit_union(decl: UnionDecl) -> list[str]:
    name = decl.name
    out = doc_lines(decl.description, "")
    out.append(f"[JsonConverter(typeof(JsonUnionConverter<{name}>))]")
    out.append(f"public sealed class {name} : JsonUnion, IJsonUnion<{name}>")
    out.append("{")
    out.append(f"    private {name}(JsonElement json)")
    out.append("        : base(json)")
    out.append("    {")
    out.append("    }")
    for value, cs_name, description in decl.strings:
        out.append("")
        out += doc_lines(description or f"<c>{value}</c>", "    ")
        out.append(f"    public static {name} {cs_name} {{ get; }} = new(StringElement({cs_string(value)}));")
    out.append("")
    out.append("    /// <summary>從原始 JSON 建立，例如協定新增、這個 SDK 還沒有對應成員的值。</summary>")
    out.append(f"    public static {name} FromJson(JsonElement json) => new(json.Clone());")
    if decl.free_string:
        out.append("")
        out.append("    /// <summary>以字串建立。</summary>")
        out.append(f"    public static {name} FromString(string value) => new(StringElement(value));")
    if decl.integer:
        out.append("")
        out.append("    /// <summary>以整數建立。</summary>")
        out.append(f"    public static {name} FromInt64(long value) => new(Int64Element(value));")
        out.append("")
        out.append("    /// <summary>值是整數時取出整數，否則是 <see langword=\"null\"/>。</summary>")
        out.append("    public long? Int64Value => Raw.ValueKind == JsonValueKind.Number && Raw.TryGetInt64(out var number) ? number : null;")
    seen = set()
    for cs_type in decl.others:
        if cs_type in seen or cs_type == "JsonNode":
            continue
        seen.add(cs_type)
        suffix = union_method_suffix(cs_type)
        out.append("")
        out.append(f"    /// <summary>以 <c>{xml_escape(cs_type)}</c> 變體建立。</summary>")
        out.append(f"    public static {name} From({cs_type} value) => new(ToElement(value));")
        out.append("")
        out.append(f"    /// <summary>值符合 <c>{xml_escape(cs_type)}</c> 變體時取出，否則是 <see langword=\"null\"/>。</summary>")
        out.append(f"    public {cs_type}? As{suffix}() => As<{cs_type}>();")
    out.append("}")
    return out


def emit_context(gen: Generator, extra_types: list[str]) -> list[str]:
    types = []
    for name in gen.order:
        types.append(name)
    for cs_type in extra_types:
        if cs_type not in types:
            types.append(cs_type)
    out = ["/// <summary>協定型別的 System.Text.Json source-gen context。屬性名稱照 schema；值是 null 的選填欄位不送出。</summary>",
           "[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]"]
    for t in types:
        out.append(f"[JsonSerializable(typeof({t}))]")
    out.append("public sealed partial class ProtocolJsonContext : JsonSerializerContext")
    out.append("{")
    out.append("}")
    return out


def collect_list_types(gen: Generator) -> list[str]:
    """union 的 From/As 用到的集合型別也要在 context 註冊。"""
    extra = []
    for name in gen.order:
        decl = gen.decls[name]
        if isinstance(decl, UnionDecl):
            for cs_type in decl.others:
                if cs_type.startswith("IReadOnly") and cs_type not in extra:
                    extra.append(cs_type)
    return extra


def emit_registry(gen: Generator, notifications: list[tuple[str, str]], requests: list[tuple[str, str, str | None]],
                  version: str) -> list[str]:
    out = []
    out.append("/// <summary>app-server 送來的通知方法名稱。</summary>")
    out.append("public static class ServerNotificationMethods")
    out.append("{")
    for index, (method, _) in enumerate(notifications):
        if index:
            out.append("")
        out.append(f"    /// <summary><c>{method}</c></summary>")
        out.append(f"    public const string {pascal(method)} = {cs_string(method)};")
    out.append("}")
    out.append("")
    out.append("/// <summary>app-server 送來、要由 client 回覆的請求方法名稱。</summary>")
    out.append("public static class ServerRequestMethods")
    out.append("{")
    for index, (method, _, _) in enumerate(requests):
        if index:
            out.append("")
        out.append(f"    /// <summary><c>{method}</c></summary>")
        out.append(f"    public const string {pascal(method)} = {cs_string(method)};")
    out.append("}")
    out.append("")

    def has_prop(type_name: str, json_name: str, cs_type: str | None = None) -> Prop | None:
        decl = gen.decls.get(type_name)
        if not isinstance(decl, RecordDecl):
            return None
        for prop in decl.props:
            if prop.json_name == json_name and (cs_type is None or prop.cs_type == cs_type):
                return prop
        return None

    out.append("/// <summary>通知與 server 請求的方法對應。對應 Python SDK 的 <c>generated/notification_registry.py</c>。</summary>")
    out.append("internal static class ProtocolRegistry")
    out.append("{")
    out.append(f"    /// <summary>產生型別時用的 Codex CLI 版本。</summary>")
    out.append(f"    public const string CodexVersion = {cs_string(version)};")
    out.append("")
    out.append("    /// <summary>把通知的 params 解析成對應的型別；不認得的方法回傳 <see langword=\"null\"/>。</summary>")
    out.append("    public static object? DeserializeNotification(string method, JsonElement parameters) => method switch")
    out.append("    {")
    for method, type_name in notifications:
        out.append(f"        {cs_string(method)} => parameters.Deserialize(ProtocolJsonContext.Default.{type_name}),")
    out.append("        _ => null,")
    out.append("    };")
    out.append("")
    out.append("    /// <summary>把 server 請求的 params 解析成對應的型別；不認得的方法回傳 <see langword=\"null\"/>。</summary>")
    out.append("    public static object? DeserializeServerRequest(string method, JsonElement parameters) => method switch")
    out.append("    {")
    for method, params_type, _ in requests:
        out.append(f"        {cs_string(method)} => parameters.Deserialize(ProtocolJsonContext.Default.{params_type}),")
    out.append("        _ => null,")
    out.append("    };")
    out.append("")
    out.append("    /// <summary>通知所屬的 turn：有 <c>turnId</c> 欄位就用它，有 <c>turn</c> 物件就用它的 <c>id</c>。</summary>")
    out.append("    public static string? NotificationTurnId(object payload) => payload switch")
    out.append("    {")
    # 好幾個通知方法可能共用同一個 payload 型別，switch 每個型別只能出現一次
    payload_types = list(dict.fromkeys(type_name for _, type_name in notifications))
    for type_name in payload_types:
        if has_prop(type_name, "turnId", "string"):
            out.append(f"        {type_name} p => p.TurnId,")
        elif (prop := has_prop(type_name, "turn")) and prop.cs_type == "Turn":
            accessor = "p.Turn?.Id" if prop.nullable else "p.Turn.Id"
            out.append(f"        {type_name} p => {accessor},")
    out.append("        _ => null,")
    out.append("    };")
    out.append("")
    out.append("    /// <summary>通知所屬的 thread：有 <c>threadId</c> 欄位就用它。</summary>")
    out.append("    public static string? NotificationThreadId(object payload) => payload switch")
    out.append("    {")
    for type_name in payload_types:
        if has_prop(type_name, "threadId", "string"):
            out.append(f"        {type_name} p => p.ThreadId,")
    out.append("        _ => null,")
    out.append("    };")
    out.append("}")
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--from-codex", metavar="CODEX", help="先用這個 codex 執行檔產生 schema 並更新 schema/")
    args = parser.parse_args()

    if args.from_codex:
        update_schema(args.from_codex)

    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    schema = json.loads(SCHEMA_FILE.read_text(encoding="utf-8"))
    defs = load_definitions(schema)
    gen = Generator(defs)

    notifications = []
    for variant in defs["ServerNotification"]["oneOf"]:
        method = variant["properties"]["method"]["enum"][0]
        notifications.append((method, gen.ref(variant["properties"]["params"]["$ref"])))

    requests = []
    for variant in defs["ServerRequest"]["oneOf"]:
        method = variant["properties"]["method"]["enum"][0]
        params_name = ref_name(variant["properties"]["params"]["$ref"])
        response_name = params_name.replace("Params", "Response")
        params_type = gen.definition(params_name)
        response_type = gen.definition(response_name) if response_name in defs else None
        requests.append((method, params_type, response_type))

    for method, params_name, response_name in CLIENT_METHODS:
        for type_name in (params_name, response_name):
            if type_name is None:
                continue
            if type_name not in defs:
                raise SystemExit(f"schema 裡沒有 {method} 用的 {type_name}")
            gen.definition(type_name)

    leaked = SKIP_DEFINITIONS & set(gen.decls)
    if leaked:
        raise SystemExit(f"不該產生的定義被參考到了：{sorted(leaked)}")

    body = []
    for name in gen.order:
        decl = gen.decls[name]
        if isinstance(decl, RecordDecl):
            body.append(emit_record(decl))
        elif isinstance(decl, EnumDecl):
            body.append(emit_enum(decl))
        elif isinstance(decl, TaggedDecl):
            body.append(emit_tagged(decl))
        elif isinstance(decl, UnionDecl):
            body.append(emit_union(decl))

    header = HEADER.format(version=version, namespace=NAMESPACE)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    write(OUT_DIR / "ProtocolTypes.g.cs", header + "\n" + "\n\n".join("\n".join(b) for b in body) + "\n")
    write(OUT_DIR / "ProtocolJsonContext.g.cs",
          header + "\n" + "\n".join(emit_context(gen, ["string", "long", "JsonElement", "JsonNode", "JsonObject"]
                                                     + collect_list_types(gen))) + "\n")
    write(OUT_DIR / "ProtocolRegistry.g.cs",
          header + "\n" + "\n".join(emit_registry(gen, notifications, requests, version)) + "\n")

    counts = {}
    for decl in gen.decls.values():
        counts[type(decl).__name__] = counts.get(type(decl).__name__, 0) + 1
    print(f"Codex CLI {version}：產生 {len(gen.decls)} 個型別 {counts}，"
          f"{len(notifications)} 種通知、{len(requests)} 種 server 請求")
    return 0


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="\n")


def update_schema(codex: str) -> None:
    """用指定的 codex 產生穩定版（不含 experimental）的 schema，跟 Python SDK 用的一樣。"""
    version_output = subprocess.run([codex, "--version"], capture_output=True, text=True, check=True).stdout
    match = re.search(r"(\d+\.\d+\.\d+\S*)", version_output)
    if match is None:
        raise SystemExit(f"讀不到 codex 的版本：{version_output!r}")
    with tempfile.TemporaryDirectory(prefix="codex-schema-") as directory:
        subprocess.run([codex, "app-server", "generate-json-schema", "--out", directory], check=True)
        shutil.copyfile(Path(directory) / SCHEMA_FILE.name, SCHEMA_FILE)
    VERSION_FILE.write_text(match.group(1) + "\n", encoding="utf-8", newline="\n")
    print(f"已更新 schema：Codex CLI {match.group(1)}")


if __name__ == "__main__":
    sys.exit(main())
