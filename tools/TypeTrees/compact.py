# Compacts an AssetRipper TypeTreeDumps InfoJson dump (github.com/AssetRipper/TypeTreeDumps,
# InfoJson/<unity version>.json) into src/OniMoono.Assets/Data/typetrees-<version>.json.gz:
#   python3 -I tools/TypeTrees/compact.py <dump>.json | gzip -9n > src/OniMoono.Assets/Data/typetrees-<version>.json.gz
# Field names, types, sizes and flags of each class's release tree only. Reproduces the
# 6000.3.5f2 file byte for byte.
import json, sys

d = json.load(open(sys.argv[1]))


def node(n):
    return {
        "T": n["TypeName"],
        "N": n["Name"],
        "S": n["ByteSize"],
        "V": n["Version"],
        "F": n["TypeFlags"],
        "M": n["MetaFlag"],
        "C": [node(c) for c in n["SubNodes"]],
    }


out = {
    "Version": d["Version"],
    "Strings": {str(s["Index"]): s["String"] for s in d["Strings"]},
    "Classes": [
        {
            "Name": c["Name"],
            "Id": c["TypeID"],
            "Base": c["Base"],
            "Abstract": c["IsAbstract"],
            "Tree": node(c["ReleaseRootNode"]) if c.get("ReleaseRootNode") else None,
        }
        for c in d["Classes"]
    ],
}
json.dump(out, sys.stdout, separators=(",", ":"), ensure_ascii=False)
