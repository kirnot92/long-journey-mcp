import hashlib
import json
import sqlite3
import sys
from collections import defaultdict
from pathlib import Path


def scalar(db, sql):
    return db.execute(sql).fetchone()[0]


def corpus(path):
    db = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    summary = {
        "sources": dict(db.execute("select status,count(*) from sources group by status")),
        "memories_by_depth": dict(db.execute("select depth,count(*) from memories group by depth")),
        "relations": dict(db.execute("select kind,count(*) from relations group by kind")),
        "runs": [{"kind": row[0], "status": row[1], "count": row[2]}
                 for row in db.execute("select kind,status,count(*) from runs group by kind,status")],
        "rejected_proposals": scalar(db, "select count(*) from rejected_proposals"),
        "settled_usd": float(scalar(db, "select coalesce(sum(cast(actual_usd as real)),0) from api_calls")),
        "reserved_usd": float(scalar(db, "select coalesce(sum(cast(reserved_usd as real)),0) from api_calls where actual_usd is null")),
        "api_calls": scalar(db, "select count(*) from api_calls"),
        "operations": [{"operation": row[0], "calls": row[1], "settled_usd": float(row[2]), "reserved_usd": float(row[3])}
                       for row in db.execute("""
                         select operation,count(*),coalesce(sum(cast(actual_usd as real)),0),
                           coalesce(sum(case when actual_usd is null then cast(reserved_usd as real) else 0 end),0)
                         from api_calls group by operation order by operation
                       """)],
        "created_by_run": dict(db.execute("""
          select r.kind,count(*) from memories m join runs r on r.id=m.dream_revision
          where m.depth>0 group by r.kind
        """)),
    }
    checkpoint = db.execute("select value from state where key='benchmark.replay.v1'").fetchone()
    if checkpoint:
        summary["replay_checkpoint"] = json.loads(checkpoint[0])
    db.close()
    return summary


def main():
    run = Path(sys.argv[1])
    dataset = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    id_by_hash = {hashlib.sha256(q["question_id"].encode()).hexdigest()[:24]: q for q in dataset}
    report = {"questions": [], "totals": {"api_calls": 0, "settled_usd": 0.0, "reserved_usd": 0.0}}
    operation_totals = defaultdict(lambda: {"calls": 0, "settled_usd": 0.0, "reserved_usd": 0.0})
    for question_dir in sorted((run / "questions").iterdir()):
        q = id_by_hash[question_dir.name]
        entry = {"question_id": q["question_id"], "question_type": q["question_type"],
                 "expected_sessions": len(q["haystack_sessions"]), "conditions": {}}
        for condition in ("remember-only", "full-long-journey"):
            path = question_dir / condition / "memory.db"
            if not path.exists():
                continue
            value = corpus(path)
            entry["conditions"][condition] = value
            for key in ("api_calls", "settled_usd", "reserved_usd"):
                report["totals"][key] += value[key]
            for operation in value["operations"]:
                total = operation_totals[operation["operation"]]
                for key in ("calls", "settled_usd", "reserved_usd"):
                    total[key] += operation[key]
        baseline = question_dir / "remember-only" / "memory.db"
        full = question_dir / "full-long-journey" / "memory.db"
        if baseline.exists() and full.exists():
            db = sqlite3.connect(f"file:{baseline.as_posix()}?mode=ro", uri=True)
            db.execute("attach database ? as full", (str(full),))
            entry["shared_depth0_mismatches"] = scalar(db, """
              select count(*) from (
                select id,content,source_ref,created_at from memories where depth=0
                except select id,content,source_ref,created_at from full.memories where depth=0
              )
            """) + scalar(db, """
              select count(*) from (
                select id,content,source_ref,created_at from full.memories where depth=0
                except select id,content,source_ref,created_at from memories where depth=0
              )
            """)
            db.close()
        report["questions"].append(entry)
    report["operations"] = [{"operation": key, **value} for key, value in sorted(operation_totals.items())]
    report["totals"]["settled_usd"] = round(report["totals"]["settled_usd"], 8)
    report["totals"]["reserved_usd"] = round(report["totals"]["reserved_usd"], 8)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
