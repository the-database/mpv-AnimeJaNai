import configparser
import os
import re
import math


bools = {"logging"}
floats = set()

# Current animejanai.conf schema version. Bump when adding a migration to _migrate_global below.
# A config with no config_version is treated as version 1 (the pre-versioning schema).
CONFIG_VERSION = 2

# trt_engine_settings values shipped as the default in past releases. If a user's config still
# carries one of these verbatim they never customized it, so migration drops the key and lets the
# current code default (animejanai_core.DEFAULT_TRT_ENGINE_SETTINGS, which is static) govern. A
# genuinely customized value is left untouched.
LEGACY_DEFAULT_TRT_ENGINE_SETTINGS = {
    "--stronglyTyped --optShapes=input:%video_resolution% --inputIOFormats=fp16:chw "
    "--outputIOFormats=fp16:chw --builderOptimizationLevel=5 "
    "--tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT --skipInference",
}


def _migrate_global(parser):
    """Apply in-memory, non-destructive migrations to [global] so playback uses current defaults
    even when the on-disk animejanai.conf predates them. The conf editor performs the durable
    rewrite; here we only adjust the parsed values for this run. Keyed on [global] config_version
    (absent => 1)."""
    if not parser.has_section("global"):
        return

    g = parser["global"]
    try:
        version = int(g.get("config_version", "1"))
    except ValueError:
        version = 1

    # v1 -> v2: stop forcing the old materialized trt_engine_settings default so the static code
    # default applies, unless the user customized the value.
    if version < 2:
        val = g.get("trt_engine_settings")
        if val is not None and val.strip() in LEGACY_DEFAULT_TRT_ENGINE_SETTINGS:
            del g["trt_engine_settings"]

    g["config_version"] = str(CONFIG_VERSION)


def _apply_default_preset(conf, parser):
    """Switch the built-in default profiles (slots 1001-1003) to the sharp model variants when
    [global] default_preset=sharp. Standard and sharp HD model filenames differ only by the
    _HD_V3.1_ vs _HD_V3.1Sharp1_ token; the SD model has no sharp variant and is left alone.
    Benchmark slots (1010-1011) are intentionally not affected."""
    preset = "standard"
    if parser.has_section("global"):
        preset = parser["global"].get("default_preset", "standard").strip().lower()
    if preset != "sharp":
        return

    for slot in ("slot_1001", "slot_1002", "slot_1003"):
        for key, chain in conf[slot].items():
            if not key.startswith("chain_"):
                continue
            for model in chain.get("models", []):
                name = model.get("name")
                if name and "_HD_V3.1_" in name:
                    model["name"] = name.replace("_HD_V3.1_", "_HD_V3.1Sharp1_")


def parse_value(key, value):
    is_bool = key in bools
    is_float = key in floats

    if is_bool:
        return True if value.casefold() == "yes".casefold() else False
    elif is_float:
        return float(value)

    return value


def parse_bool(value):
    return True if value.casefold() == "yes".casefold() else False


def read_config_by_chain(flat_conf, section, chain, num_models):
    min_resolution = flat_conf[section].get(f"chain_{chain}_min_resolution", "0x0")
    max_resolution = flat_conf[section].get(f"chain_{chain}_max_resolution", "0x0")

    if min_resolution == "":
        min_resolution = "0x0"
    if max_resolution == "":
        max_resolution = "infxinf"

    min_width, min_height = [float(px) for px in min_resolution.split("x")]
    max_width, max_height = [float(px) for px in max_resolution.split("x")]

    if max_height == 0:
        max_height = float("inf")
    if max_width == 0:
        max_width = float("inf")

    max_fps = float(flat_conf[section].get(f"chain_{chain}_max_fps", "inf"))

    if max_fps == 0:
        max_fps = float("inf")

    return {
        "min_px": min_width * min_height,
        "max_px": max_width * max_height,
        "min_resolution": min_resolution,
        "max_resolution": max_resolution,
        "min_fps": float(flat_conf[section].get(f"chain_{chain}_min_fps", 0)),
        "max_fps": max_fps,
        "models": [
            read_config_by_chain_model(flat_conf, section, chain, i)
            for i in range(1, num_models + 1)
        ],
        "rife": parse_bool(flat_conf[section].get(f"chain_{chain}_rife", "no")),
        "rife_factor_numerator": int(
            float(flat_conf[section].get(f"chain_{chain}_rife_factor_numerator", 1))
        ),
        "rife_factor_denominator": int(
            float(flat_conf[section].get(f"chain_{chain}_rife_factor_denominator", 1))
        ),
        "rife_model": int(
            float(flat_conf[section].get(f"chain_{chain}_rife_model", 414))
        ),
        "rife_ensemble": parse_bool(
            flat_conf[section].get(f"chain_{chain}_rife_ensemble", "no")
        ),
        "rife_scene_detect_threshold": float(
            flat_conf[section].get(f"chain_{chain}_rife_scene_detect_threshold", 0.150)
        ),
    }


def read_config_by_chain_model(flat_conf, section, chain, model):
    return {
        "resize_factor_before_upscale": float(
            flat_conf[section].get(
                f"chain_{chain}_model_{model}_resize_factor_before_upscale", 100
            )
        ),
        "resize_height_before_upscale": float(
            flat_conf[section].get(
                f"chain_{chain}_model_{model}_resize_height_before_upscale", 0
            )
        ),
        "name": flat_conf[section].get(f"chain_{chain}_model_{model}_name", None),
    }


def read_config():
    parser = configparser.ConfigParser(interpolation=None)
    flat_conf = {}
    conf = {
        "slot_1001": {
            "chain_1": {
                "max_fps": 31.0,
                "max_px": 2073600.0,
                "max_resolution": "1920x1080",
                "min_fps": 0.0,
                "min_px": 921600.0,
                "min_resolution": "1280x720",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "chain_2": {
                "max_fps": 31.0,
                "max_px": 921600.0,
                "max_resolution": "1280x720",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op23_dynamo",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "chain_3": {
                "max_fps": 61.0,
                "max_px": 2073600.0,
                "max_resolution": "1920x1080",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "profile_name": "Quality",
        },
        "slot_1002": {
            "chain_1": {
                "max_fps": 31.0,
                "max_px": 2073600.0,
                "max_resolution": "1920x1080",
                "min_fps": 0.0,
                "min_px": 921600.0,
                "min_resolution": "1280x720",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "chain_2": {
                "max_fps": 31.0,
                "max_px": 921600.0,
                "max_resolution": "1280x720",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op23_dynamo",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "profile_name": "Balanced",
        },
        "slot_1003": {
            "chain_1": {
                "max_fps": 31.0,
                "max_px": 2073600.0,
                "max_resolution": "1920x1080",
                "min_fps": 0.0,
                "min_px": 921600.0,
                "min_resolution": "1280x720",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "chain_2": {
                "max_fps": 31.0,
                "max_px": 921600.0,
                "max_resolution": "1280x720",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op23_dynamo",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
            },
            "profile_name": "Performance",
        },
        "slot_1010": {
            "chain_1": {
                "max_fps": float("inf"),
                "max_px": float("inf"),
                "max_resolution": "infxinf",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
                "rife_ensemble": False,
                "rife_factor_denominator": 1,
                "rife_factor_numerator": 1,
                "rife_model": 414,
                "rife_scene_detect_threshold": 0.15,
            },
            "profile_name": "New Profile",
        },
        "slot_1011": {
            "chain_1": {
                "max_fps": float("inf"),
                "max_px": float("inf"),
                "max_resolution": "infxinf",
                "min_fps": 0.0,
                "min_px": 0.0,
                "min_resolution": "0x0",
                "models": [
                    {
                        "name": "2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16",
                        "resize_factor_before_upscale": 100.0,
                        "resize_height_before_upscale": 0.0,
                    }
                ],
                "rife": False,
                "rife_ensemble": False,
                "rife_factor_denominator": 1,
                "rife_factor_numerator": 1,
                "rife_model": 414,
                "rife_scene_detect_threshold": 0.15,
            },
            "profile_name": "New Profile",
        },
    }
    parser.read(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "../animejanai.conf")
    )

    _migrate_global(parser)
    _apply_default_preset(conf, parser)

    all_keys_by_section = {}

    for section in parser.sections():
        if section not in flat_conf:
            conf[section] = {}
            flat_conf[section] = {}

        if section not in all_keys_by_section:
            all_keys_by_section[section] = set()

        for key in parser[section]:
            all_keys_by_section[section].add(key)
            flat_conf[section][key] = parser[section][key]

    for section in all_keys_by_section:
        all_keys = all_keys_by_section[section]

        all_chain_keys = {
            re.match(r"chain_(\d+)_.*", key).group(0)
            for key in all_keys
            if re.match(r"chain_(\d+)_.*", key) is not None
        }

        all_chains = {
            int(re.match(r"chain_(\d+)_.*", key).group(1))
            for key in all_keys
            if re.match(r"chain_(\d+)_.*", key) is not None
        }

        all_other = [key for key in all_keys if key not in all_chain_keys]

        for i, chain in enumerate(all_chains):
            all_models = {
                int(re.match(rf"chain_{chain}_model_(\d+).*", key).group(1))
                for key in all_keys
                if re.match(rf"chain_{chain}_model_(\d+).*", key) is not None
            }

            conf[section][f"chain_{int(chain)}"] = read_config_by_chain(
                flat_conf, section, int(chain), len(all_models)
            )

        for key in all_other:
            conf[section][key] = parse_value(key, parser[section][key])

    return conf
