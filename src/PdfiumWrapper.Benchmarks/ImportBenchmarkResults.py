#!/usr/bin/env python3
import argparse
import csv
import json
import sqlite3
from datetime import datetime
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser(
        description="Import BenchmarkDotNet CSV results into benchmark.db."
    )
    parser.add_argument("results_dir", help="Directory containing BenchmarkDotNet CSV reports.")
    parser.add_argument(
        "--db",
        dest="db_path",
        required=True,
        help="Path to the SQLite database file to create/update.",
    )
    parser.add_argument(
        "--run-label",
        default="benchmark",
        help="Label stored in the runs table for this import.",
    )
    return parser.parse_args()


def parse_numeric(value):
    if value is None or value == "":
        return None
    return float(value.replace(",", ""))


def benchmark_name_from_file(name):
    suffix = "-report.csv"
    core = name[:-len(suffix)] if name.endswith(suffix) else name
    parts = core.split(".")
    return parts[-1] if parts else core


def to_display_path(path):
    try:
        return str(path.relative_to(Path.cwd()))
    except ValueError:
        return str(path)


def main():
    args = parse_args()
    results_dir = Path(args.results_dir).resolve()
    db_path = Path(args.db_path).resolve()
    csv_files = sorted(results_dir.glob("*.csv"))

    if not csv_files:
        raise SystemExit(f"No CSV files found in {results_dir}")

    mtimes = [datetime.fromtimestamp(path.stat().st_mtime) for path in csv_files]
    run_id = min(mtimes).strftime("%Y%m%d%H%M")
    first_mtime = min(mtimes).isoformat(timespec="minutes")
    last_mtime = max(mtimes).isoformat(timespec="minutes")

    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.execute("PRAGMA foreign_keys = ON")
    conn.executescript(
        """
        CREATE TABLE IF NOT EXISTS runs (
            run_id TEXT PRIMARY KEY,
            run_label TEXT NOT NULL,
            source_dir TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            first_file_mtime TEXT NOT NULL,
            last_file_mtime TEXT NOT NULL,
            file_count INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_results (
            id INTEGER PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
            benchmark_file TEXT NOT NULL,
            benchmark_name TEXT NOT NULL,
            source_csv TEXT NOT NULL,
            method TEXT NOT NULL,
            document TEXT NOT NULL,
            job TEXT,
            runtime TEXT,
            platform TEXT,
            mean_ms REAL,
            error_ms REAL,
            stddev_ms REAL,
            median_ms REAL,
            raw_row_json TEXT NOT NULL,
            UNIQUE(run_id, benchmark_file, method, document)
        );

        CREATE INDEX IF NOT EXISTS ix_benchmark_results_lookup
        ON benchmark_results (benchmark_name, method, document, run_id);
        """
    )

    conn.execute("DELETE FROM benchmark_results WHERE run_id = ?", (run_id,))
    conn.execute("DELETE FROM runs WHERE run_id = ?", (run_id,))
    conn.execute(
        """
        INSERT INTO runs (
            run_id, run_label, source_dir, imported_at,
            first_file_mtime, last_file_mtime, file_count
        ) VALUES (?, ?, ?, ?, ?, ?, ?)
        """,
        (
            run_id,
            args.run_label,
            to_display_path(results_dir),
            datetime.now().isoformat(timespec="seconds"),
            first_mtime,
            last_mtime,
            len(csv_files),
        ),
    )

    inserted_rows = 0
    for csv_path in csv_files:
        benchmark_file = csv_path.name
        benchmark_name = benchmark_name_from_file(benchmark_file)
        with csv_path.open(newline="") as handle:
            reader = csv.DictReader(handle)
            for row in reader:
                conn.execute(
                    """
                    INSERT INTO benchmark_results (
                        run_id, benchmark_file, benchmark_name, source_csv,
                        method, document, job, runtime, platform,
                        mean_ms, error_ms, stddev_ms, median_ms, raw_row_json
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        run_id,
                        benchmark_file,
                        benchmark_name,
                        to_display_path(csv_path),
                        row.get("Method", ""),
                        row.get("Document", ""),
                        row.get("Job"),
                        row.get("Runtime"),
                        row.get("Platform"),
                        parse_numeric(row.get("Mean [ms]")),
                        parse_numeric(row.get("Error [ms]")),
                        parse_numeric(row.get("StdDev [ms]")),
                        parse_numeric(row.get("Median [ms]")),
                        json.dumps(row, separators=(",", ":"), ensure_ascii=True),
                    ),
                )
                inserted_rows += 1

    conn.commit()
    conn.close()

    print(f"Imported run {run_id} ({inserted_rows} rows) into {db_path}")


if __name__ == "__main__":
    main()
