# -*- coding: utf-8 -*-
"""Сводка ETW-трейса старта: какие процессы стартовали и кто дергал диск.

Разбирает XML от tracerpt.exe потоково (файлы бывают сотни мегабайт).
Использование:
    py -3 tools/perf/Summarize-Etl.py <трейс.xml> [--root-pid <pid>]

Выводит: дерево запусков процессов с командными строками, счетчики
событий Kernel-File/Kernel-Disk по процессам, топ файлов по обращениям.
"""

import argparse
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict


def local(tag):
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def event_data(event):
    data = {}
    for elem in event.iter():
        if local(elem.tag) == "Data" and "Name" in elem.attrib:
            data[elem.attrib["Name"]] = (elem.text or "").strip()
    return data


def main():
    parser = argparse.ArgumentParser(description="Сводка ETW-трейса старта K-Tools.")
    parser.add_argument("xml", help="XML от tracerpt.exe")
    parser.add_argument("--root-pid", type=int, default=0,
                        help="PID корневого KTools.App.exe (подсветить его дерево)")
    parser.add_argument("--top-files", type=int, default=20)
    args = parser.parse_args()

    starts = []          # (pid, ppid, image, cmd)
    images = {}          # pid -> image
    file_events = Counter()       # (pid, provider) -> count
    file_hits = Counter()         # filename -> count
    disk_events = Counter()       # pid -> count

    context = ET.iterparse(args.xml, events=("end",))
    for _, elem in context:
        if local(elem.tag) != "Event":
            continue
        provider = ""
        event_id = ""
        pid = 0
        for child in elem:
            name = local(child.tag)
            if name == "System":
                for sub in child:
                    sname = local(sub.tag)
                    if sname == "Provider":
                        provider = sub.attrib.get("Name", "")
                    elif sname == "EventID":
                        event_id = (sub.text or "").strip()
                    elif sname == "Execution":
                        try:
                            pid = int(sub.attrib.get("ProcessID", "0"))
                        except ValueError:
                            pid = 0
        data = event_data(elem)

        if "Kernel-Process" in provider and event_id in ("1", "2"):
            image = data.get("ImageName") or data.get("ImageFileName") or ""
            try:
                subject = int(data.get("ProcessID", "0") or 0)
            except ValueError:
                subject = 0
            if image and subject:
                images[subject] = image.split("\\")[-1]
            if event_id == "1":
                starts.append({
                    "pid": subject or pid,
                    "ppid": int(data.get("ParentID", "0") or 0),
                    "image": image.split("\\")[-1] if image else "?",
                    "cmd": data.get("CommandLine", ""),
                })
        elif "Kernel-File" in provider:
            file_events[(pid, event_id)] += 1
            fname = data.get("FileName", "")
            if fname:
                file_hits[fname] += 1
        elif "Kernel-Disk" in provider:
            disk_events[pid] += 1
        elem.clear()

    by_pid = {s["pid"]: s for s in starts}
    print(f"Запусков процессов в трейсе: {len(starts)}")
    interesting = [s for s in starts if s["ppid"] == args.root_pid] if args.root_pid else []
    if interesting:
        print(f"\nДочерние процессы корня (pid {args.root_pid}): {len(interesting)}")
        for s in interesting:
            cmd = f"  {s['cmd']}" if s["cmd"] else ""
            print(f"  pid={s['pid']} {s['image']}{cmd}")
    else:
        print("\nВсе запуски (первые 30):")
        for s in starts[:30]:
            print(f"  pid={s['pid']} ppid={s['ppid']} {s['image']} {s['cmd']}")

    per_proc = defaultdict(int)
    for (pid, _eid), count in file_events.items():
        per_proc[pid] += count
    print(f"\nФайловых событий всего: {sum(per_proc.values())}")
    ranked = sorted(per_proc.items(), key=lambda kv: kv[1], reverse=True)[:15]
    for pid, count in ranked:
        print(f"  pid={pid} ({by_pid.get(pid, {}).get('image') or images.get(pid, '?')}): {count}")

    print(f"\nТоп-{args.top_files} файлов по обращениям:")
    for fname, count in file_hits.most_common(args.top_files):
        print(f"  {count:7d}  {fname}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
