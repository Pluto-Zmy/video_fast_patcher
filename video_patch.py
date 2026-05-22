import json
import subprocess
from pathlib import Path


def run(cmd):
    print(" ".join(cmd))
    subprocess.run(cmd, check=True)


def safe_name(index):
    return f"part_{index:04d}.mp4"


def ffmpeg_base(ffmpeg_config):
    args = ["ffmpeg"]

    if ffmpeg_config.get("overwrite", True):
        args.append("-y")

    loglevel = ffmpeg_config.get("loglevel")
    if loglevel:
        args += ["-loglevel", loglevel]

    return args


def main():

    config_path = "config.json"

    with open(config_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    input_video = cfg["input"]
    output_video = cfg["output"]

    workdir = Path(cfg.get("workdir", "_patch_tmp"))
    workdir.mkdir(exist_ok=True)

    ffmpeg_cfg = cfg.get("ffmpeg", {})
    concat_cfg = cfg.get("concat", {})

    segments = sorted(cfg["segments"], key=lambda x: x["start"])

    parts = []

    cursor = "00:00:00.000"
    part_index = 0

    for seg in segments:

        start = seg["start"]
        end = seg["end"]
        patch = seg["patch"]

        # 导出 patch 前面的正常部分
        if start != cursor:

            part_file = workdir / safe_name(part_index)

            cmd = ffmpeg_base(ffmpeg_cfg) + [
                "-i",
                input_video,
                "-ss",
                cursor,
                "-to",
                start,
                "-c",
                "copy",
                str(part_file),
            ]

            run(cmd)

            parts.append(part_file)

            part_index += 1

        # 插入 patch
        patch_path = Path(patch)

        if not patch_path.exists():
            raise FileNotFoundError(f"Patch 不存在: {patch}")

        parts.append(patch_path)

        cursor = end

    # 最后一段尾巴
    tail_file = workdir / safe_name(part_index)

    cmd = ffmpeg_base(ffmpeg_cfg) + [
        "-i",
        input_video,
        "-ss",
        cursor,
        "-c",
        "copy",
        str(tail_file),
    ]

    run(cmd)

    parts.append(tail_file)

    # concat 列表
    concat_list = workdir / "concat_list.txt"

    with open(concat_list, "w", encoding="utf-8") as f:
        for p in parts:
            f.write(f"file '{p.resolve().as_posix()}'\n")

    # 拼接
    concat_mode = concat_cfg.get("mode", "copy")

    cmd = ffmpeg_base(ffmpeg_cfg) + [
        "-f",
        "concat",
        "-safe",
        "0",
        "-i",
        str(concat_list),
    ]

    if concat_mode == "copy":
        cmd += ["-c", "copy"]
    else:
        cmd += ["-c:v", "libx264", "-c:a", "aac"]

    cmd.append(output_video)

    run(cmd)

    print(f"\n完成: {output_video}")


if __name__ == "__main__":
    main()
