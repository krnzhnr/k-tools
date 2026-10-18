# -*- coding: utf-8 -*-
"""Разбор сессионного лога K-Tools: фазы старта и шторм дочерних процессов.

Читает как JSON Lines, так и текстовый формат (LogFileFormat.Text).
Печатает таблицу фаз (время от первого события) и сводку process.started
по утилитам + суммарные durationMs из process.exit.

Использование:
    py -3 tools/perf/Analyze-StartupLog.py %LOCALAPPDATA%\\KTools\\logs\\ktools_....log
    py -3 tools/perf/Analyze-StartupLog.py <файл> --markers app.started system.snapshot.completed
"""

import argparse
import json
import re
import sys
from collections import Counter
from datetime import datetime, timezone

TEXT_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)\s+\|\s+.*?\|\s+.*?\|\s+(\d+)\s+\|\s+([A-Za-z0-9._-]+)"
)
LEGACY_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+\|\s+(\w+)\s+\|\s+(.*?)\s+\|\s+(.*)$"
)
DUR_RE = re.compile(r"durationMs=([0-9]+(?:\.[0-9]+)?)")
TOOL_RE = re.compile(r"tool=([A-Za-z0-9_.+-]+)")

DEFAULT_MARKERS = [
    # Актуальный формат: eventId.
    "app.started",
    "system.snapshot.completed",
    "system.os.detected",
    "system.drive.detected",
    "app.dependencies.checked",
    "encoder.capabilities.detected",
    "encoder.capabilities.init_started",
    "dependency.update_check.started",
    "dependency.update_check.completed",
    "app.update.check_started",
    "app.update.available",
    # Legacy-формат старых сборок: совпадений по тексту сообщений.
    "Запуск приложения",
    "Сведения о системе",
    "Настройки загружены",
    "hevc_nvenc",
    "Доступна более новая версия",
    "Обязательные компоненты",
]


def parse_ts(raw):
    for fmt in ("%Y-%m-%dT%H:%M:%S.%fZ", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(raw, fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    return None


def iter_events(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.lstrip(chr(0xFEFF)).rstrip("\n")
            if not line.strip():
                continue
            try:
                obj = json.loads(line)
                ts = parse_ts(obj.get("timestampUtc", ""))
                if ts is None:
                    continue
                yield {
                    "ts": ts,
                    "event": obj.get("eventId", ""),
                    "message": obj.get("message", ""),
                    "duration": obj.get("durationMs"),
                    "tool": obj.get("tool"),
                }
                continue
            except (json.JSONDecodeError, AttributeError):
                pass
            match = TEXT_RE.match(line)
            if not match:
                legacy = LEGACY_RE.match(line)
                if not legacy:
                    continue
                ts = parse_ts(legacy.group(1))
                if ts is None:
                    continue
                yield {"ts": ts, "event": "", "message": legacy.group(4),
                       "duration": None, "tool": None}
                continue
            ts = parse_ts(match.group(1))
            if ts is None:
                continue
            dur = DUR_RE.search(line)
            tool = TOOL_RE.search(line)
            yield {
                "ts": ts,
                "event": match.group(3),
                "message": line,
                "duration": float(dur.group(1)) if dur else None,
                "tool": tool.group(1) if tool else None,
            }


def main():
    parser = argparse.ArgumentParser(description="Фазы старта и процессы из лога K-Tools.")
    parser.add_argument("log", help="Путь к сессионному ktools_*.log")
    parser.add_argument("--markers", nargs="*", default=DEFAULT_MARKERS,
                        help="Маркеры фаз (eventId или подстроки).")
    args = parser.parse_args()

    events = list(iter_events(args.log))
    if not events:
        print("События не распознаны: неизвестный формат файла.")
        return 1
    events.sort(key=lambda e: e["ts"])
    t0 = events[0]["ts"]
    print(f"Событий: {len(events)}, сессия: {t0.isoformat()} .. {events[-1]['ts'].isoformat()}")

    print("\nФазы (время от первого события):")
    seen = set()
    for event in events:
        for marker in args.markers:
            if marker in seen:
                continue
            if marker in event["event"] or marker in event["message"]:
                delta = (event["ts"] - t0).total_seconds()
                print(f"  +{delta:8.2f} с  {event['event']}")
                seen.add(marker)

    started = Counter()
    durations: dict = {}
    for event in events:
        if event["event"] == "process.started":
            started[event["tool"] or "?"] += 1
        elif event["event"] == "process.exit" and event["duration"]:
            durations.setdefault(event["tool"] or "?", []).append(event["duration"])

    print(f"\nЗапуски процессов: {sum(started.values())}")
    for tool, count in started.most_common():
        durs = durations.get(tool, [])
        if durs:
            print(f"  {tool}: запусков {count}, суммарно {sum(durs):.0f} мс, среднее {sum(durs)/len(durs):.0f} мс")
        else:
            print(f"  {tool}: запусков {count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
