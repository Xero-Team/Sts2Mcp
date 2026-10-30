#!/usr/bin/env python3
"""Keep tests/GameSurface/game-surface.json in step with the mod's game bindings.

The manifest is what tests/GameSurface resolves against the installed sts2.dll.
Its failure mode is drift: someone adds a reflection string or a string-target
Harmony patch and the manifest keeps claiming the old surface is complete. This
script re-derives the entries from the C# sources and fails when the checked-in
manifest is missing one (or carries a stale line number).

    python3 scripts/test_game_surface_manifest.py          # check
    python3 scripts/test_game_surface_manifest.py --write  # regenerate

No network, no game install, no dependencies beyond the standard library.
"""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "tests" / "GameSurface" / "game-surface.json"
SKIP_DIRS = {"bin", "obj", "tests", ".git"}

MEMBER_KIND = {
    "GetField": "field",
    "GetProperty": "property",
    "GetMethod": "method",
    "GetEvent": "event",
}
HELPER_KIND = {
    "GetInstanceFieldValue": "field",
    "ClickMenuButtonField": "field",
    "GetPropertyValue": "property",
    "ReadModelText": "property",
    "GetInstanceMemberValue": "property-or-field",
    "GetMemberValue": "property-or-field",
    "GetBoolMemberValue": "property-or-field",
}
ACCESS_KIND = {
    "DeclaredMethod": "method",
    "Field": "field",
    "FieldRefAccess": "field",
    "TypeByName": "type",
}

# Sites no regex family can see: member names passed through a helper parameter, a
# candidate-name array the loop tries in turn, a patch table resolved by hand, and
# the one type looked up through a const.
EXTRAS = [
    ("Recorder/RecorderPatches.cs", 32, "AccessTools.FieldRefAccess", "_replay", "field", "CombatReplayWriter"),
    ("Recorder/RecorderPatches.cs", 34, "AccessTools.FieldRefAccess", "_startTime", "field", "RunManager"),
    ("Recorder/RecorderPatches.cs", 37, "AccessTools.Field", "_commands", "field", "DevConsole"),
    ("Recorder/RecorderPatches.cs", 40, "AccessTools.Field", "_actionQueues", "field", "ActionQueueSet"),
    ("Recorder/RecorderPatches.cs", 52, "RecorderPatches.Patch", "RecordGameAction", "method", "CombatReplayWriter"),
    ("Recorder/RecorderPatches.cs", 53, "RecorderPatches.Patch", "RecordActionResume", "method", "CombatReplayWriter"),
    ("Recorder/RecorderPatches.cs", 54, "RecorderPatches.Patch", "RecordPlayerChoice", "method", "CombatReplayWriter"),
    ("Recorder/RecorderPatches.cs", 55, "RecorderPatches.Patch", "RecordChecksum", "method", "CombatReplayWriter"),
    ("Recorder/RecorderPatches.cs", 73, "RecorderPatches.Patch", "AfterActionFinished", "method", "ActionExecutor"),
    ("McpMod.CrystalSphere.cs", 41, "AccessTools.TypeByName",
     "MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent+CrystalSphereMinigame", "type", None),
    ("McpMod.CrystalSphere.cs", 79, "AccessTools.TypeByName",
     "MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent+CrystalSphereMinigame", "type", None),
    ("McpMod.CardSelectionContext.cs", 165, "method-prefix", "From", "method-prefix", "CardSelectCmd"),
    ("McpMod.LoadingDiagnostics.cs", 148, "HarmonyPatch.ctor", ".ctor", "constructor", "NetLoadingHandle"),
    ("McpMod.StateBuilder.cs", 949, "AddMenuOptionIfVisible", "_backButton", "field", None),
    ("McpMod.StateBuilder.cs", 992, "AddMenuOptionIfVisible", "_backButton", "field", None),
    ("McpMod.StateBuilder.cs", 2083, "AddMenuOptionIfVisible", "_backButton", "field", None),
    ("McpMod.Helpers.cs", 1246, "GetVerticalPopupText", "TitleLabel", "property", None),
    ("McpMod.Helpers.cs", 1247, "GetVerticalPopupText", "BodyLabel", "property", None),
    ("McpMod.Helpers.cs", 831, "candidate-name", "SmartDescription", "property", None),
    ("McpMod.Helpers.cs", 831, "candidate-name", "DynamicDescription", "property", None),
    ("McpMod.Helpers.cs", 831, "candidate-name", "Description", "property", None),
    ("McpMod.Helpers.cs", 1379, "candidate-name", "_label", "field", None),
    ("McpMod.Helpers.cs", 1379, "candidate-name", "_textLabel", "field", None),
    ("McpMod.Helpers.cs", 1379, "candidate-name", "_title", "field", None),
    ("McpMod.Helpers.cs", 1379, "candidate-name", "_buttonLabel", "field", None),
    ("McpMod.Helpers.cs", 1386, "candidate-name", "Text", "property", None),
    ("McpMod.Helpers.cs", 1386, "candidate-name", "Label", "property", None),
    ("McpMod.Helpers.cs", 1386, "candidate-name", "Title", "property", None),
    ("McpMod.Helpers.cs", 1440, "candidate-name", "Text", "property", None),
    ("McpMod.Helpers.cs", 1440, "candidate-name", "BbcodeText", "property", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_continueButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_abandonRunButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_singleplayerButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_multiplayerButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_compendiumButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_timelineButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_settingsButton", "field", None),
    ("McpMod.StateBuilder.cs", 1139, "candidate-name", "_quitButton", "field", None),
]

# Members reached through a string type name rather than typeof.
TYPE_NAME_EXTRAS = [
    ("McpMod.Compendium.cs", 584, "Assembly.GetType", "MegaCrit.Sts2.Core.Platform.Steam.SteamUtil"),
]

# Members the mod deliberately does not require: a version-gated read with a fallback,
# or one name in a candidate list that the loop tries in turn.
OPTIONAL = {
    ("McpMod.Helpers.cs", "GetProperty", "IsPlayPhase"),
    ("McpMod.StateBuilder.cs", "GetProperty", "MarkedActIndex"),
    ("McpMod.Helpers.cs", "candidate-name", "_textLabel"),
    ("McpMod.Helpers.cs", "candidate-name", "_buttonLabel"),
    ("McpMod.Helpers.cs", "candidate-name", "Label"),
    ("McpMod.Helpers.cs", "candidate-name", "BbcodeText"),
}

# Members declared outside sts2.dll: a BCL collection or tuple. The harness cannot see
# them in the game assembly, so it reports them and does not grade them.
EXTERNAL = {
    ("McpMod.Actions.cs", "GetField", "Item1"): "bcl",
    ("McpMod.Actions.cs", "GetProperty", "Count"): "bcl",
    ("McpMod.Actions.cs", "GetMethod", "Peek"): "bcl",
    ("McpMod.Actions.cs", "GetMethod", "Pop"): "bcl",
}

# Confirmed absent in v0.111.0 (sha256 2b40d2df538d). Recorded so the gap is visible
# instead of silently passing; the fix belongs in its own change.
BROKEN = {
    ("McpMod.Compendium.cs", "Assembly.GetType", "MegaCrit.Sts2.Core.Platform.Steam.SteamUtil"):
        "Steam changed type or namespace (v0.111.0: Multiplayer.Transport.Steam.SteamUtil has no GetSteamID64)",
    ("McpMod.Compendium.cs", "GetMethod", "GetSteamID64"):
        "owner lookup targets a type that is not in this build (see the Assembly.GetType entry)",
}


def source_files():
    return sorted(
        p for p in ROOT.rglob("*.cs")
        if not any(part in SKIP_DIRS for part in p.relative_to(ROOT).parts)
    )


def line_of(text, index):
    return text.count("\n", 0, index) + 1


def extract():
    entries = []

    def add(file, line, api, member, kind, owner):
        entry = {
            "file": file,
            "line": line,
            "api": api,
            "member": member,
            "kind": kind,
            "scope": "type" if owner else "assembly",
            "type": owner,
        }
        if api == "HarmonyPatch.ctor":
            entry["arity"] = 1
        entries.append(entry)

    for path in source_files():
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8")

        # typeof(X).GetMember("name"): the owning type is stated in the source.
        for m in re.finditer(
            r'typeof\(([\w.]+)\)\s*\.\s*(GetField|GetProperty|GetMethod|GetEvent)\s*\(\s*"([^"]+)"',
            text,
        ):
            add(rel, line_of(text, m.start()), m.group(2), m.group(3), MEMBER_KIND[m.group(2)], m.group(1))

        # The same call on a runtime value: the owning type is only known at run time.
        for m in re.finditer(r"(?<!Try)\b(GetField|GetProperty|GetMethod|GetEvent)\s*\(\s*\"([^\"]+)\"", text):
            before = text[max(0, m.start() - 80):m.start()]
            if re.search(r"typeof\([\w.]+\)\s*\.\s*$", before):
                continue
            add(rel, line_of(text, m.start()), m.group(1), m.group(2), MEMBER_KIND[m.group(1)], None)

        # The mod's own member-name helpers.
        for m in re.finditer(
            r'\b(GetInstanceFieldValue|GetInstanceMemberValue|GetMemberValue|GetBoolMemberValue'
            r'|ReadModelText|GetPropertyValue|ClickMenuButtonField)\s*\(\s*[^,()]+?\s*,\s*"([^"]+)"',
            text,
        ):
            add(rel, line_of(text, m.start()), m.group(1), m.group(2), HELPER_KIND[m.group(1)], None)

        # AccessTools.*(typeof(X), "member"), and AccessTools.*(value, "member").
        for m in re.finditer(
            r'AccessTools\.(DeclaredMethod|Field|TypeByName)\s*\(\s*typeof\(([\w.]+)\)\s*,\s*"([^"]+)"',
            text,
        ):
            add(rel, line_of(text, m.start()), "AccessTools." + m.group(1), m.group(3),
                ACCESS_KIND[m.group(1)], m.group(2))
        for m in re.finditer(r'AccessTools\.(DeclaredMethod|Field)\s*\(\s*([^,()]+?)\s*,\s*"([^"]+)"', text):
            add(rel, line_of(text, m.start()), "AccessTools." + m.group(1), m.group(3),
                ACCESS_KIND[m.group(1)], None)

        # String type names: Assembly.GetType("Ns.Type") is a rename-sensitive lookup too.
        for m in re.finditer(r'A[\w.]*\.GetType\("([^"]+)"\)', text):
            add(rel, line_of(text, m.start()), "Assembly.GetType", m.group(1), "type", None)

        # [HarmonyPatch(typeof(X), "Member")]: the family that fails silently on a rename.
        for m in re.finditer(r'\[HarmonyPatch\(\s*typeof\(([\w.]+)\)\s*,\s*"([^"]+)"\s*\)\]', text):
            add(rel, line_of(text, m.start()), "HarmonyPatch", m.group(2), "method", m.group(1))

    for file, line, api, member, kind, owner in EXTRAS:
        add(file, line, api, member, kind, owner)

    for file, line, api, member in TYPE_NAME_EXTRAS:
        add(file, line, api, member, "type", None)

    # One entry per binding, not per call site: `_backButton` read on nine screens is
    # one game member. The recorded line is the first place the source names it.
    unique = {}
    for entry in entries:
        key = (entry["file"], entry["api"], entry["member"], entry["type"])
        current = unique.get(key)
        if current is None:
            unique[key] = entry
        elif entry["line"] < current["line"]:
            current["line"] = entry["line"]
    return sorted(unique.values(), key=lambda e: (e["file"], e["line"], e["api"], e["member"]))


def classify(entries):
    for entry in entries:
        key = (entry["file"], entry["api"], entry["member"])
        entry["required"] = key not in OPTIONAL
        if key in OPTIONAL:
            entry["optional_reason"] = "version-gated or candidate-name read with a fallback"
        if key in EXTERNAL:
            entry["external"] = EXTERNAL[key]
        if key in BROKEN:
            entry["broken"] = BROKEN[key]
    return entries


def build_manifest(entries):
    return {
        "schema": 1,
        "assembly": "sts2.dll",
        "note": (
            "Derived from source by scripts/test_game_surface_manifest.py --write. Each entry "
            "is a literal member name the mod reaches by reflection, or a string/nameof Harmony "
            "target. `scope: type` names the owning type when the source states it; `scope: "
            "assembly` records that the owning type is only known at runtime, so the member must "
            "still be declared somewhere in sts2.dll."
        ),
        "entries": entries,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="rewrite the manifest from source")
    args = parser.parse_args()

    entries = classify(extract())
    manifest = build_manifest(entries)

    if args.write:
        MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {MANIFEST.relative_to(ROOT)} ({len(entries)} entries)")
        return 0

    if not MANIFEST.exists():
        print(f"FAIL manifest missing: {MANIFEST.relative_to(ROOT)}")
        return 1

    checked_in = json.loads(MANIFEST.read_text(encoding="utf-8"))
    recorded = checked_in.get("entries", [])
    key = lambda e: (e.get("file"), e.get("api"), e.get("member"), e.get("type"))
    recorded_keys = {key(e) for e in recorded}
    source_keys = {key(e) for e in entries}

    missing = sorted(source_keys - recorded_keys)
    stale = sorted(recorded_keys - source_keys)
    failures = 0

    for file, api, member, owner in missing:
        line = next(e["line"] for e in entries if key(e) == (file, api, member, owner))
        print(f"FAIL manifest missing {file}:{line} {api}(\"{member}\") - run --write")
        failures += 1
    for file, api, member, owner in stale:
        print(f"FAIL manifest has stale {file} {api}(\"{member}\") - not found in source")
        failures += 1

    # A moved call site means the line in the manifest no longer points at it. The
    # identity still matches, so this is reported separately rather than as drift.
    recorded_by_key = {key(e): e for e in recorded}
    for entry in entries:
        old = recorded_by_key.get(key(entry))
        if old is not None and old.get("line") != entry["line"]:
            print(f"FAIL manifest line for {entry['file']} {entry['api']}(\"{entry['member']}\"): "
                  f"recorded {old.get('line')}, source {entry['line']} - run --write")
            failures += 1

    if failures:
        print(f"{failures} manifest drift(s)")
        return 1

    print(f"ok   game-surface manifest matches source ({len(entries)} entries)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
