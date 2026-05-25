import os
import sys
import traceback
from datetime import datetime
from typing import Tuple

import cv2
import numpy as np
from flask import Flask, request, Response
from ultralytics import YOLO
from huggingface_hub import hf_hub_download

HOST = "127.0.0.1"
PORT = 5058

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(BASE_DIR, "ai_server_debug.log")

REPO_ID = os.environ.get("TOMATO_MODEL_REPO", "pablofntdz/yolov8-tomato-detection")
FILENAME = os.environ.get("TOMATO_MODEL_FILE", "model_hub_n.pt")
LOCAL_MODEL = os.environ.get("TOMATO_MODEL_LOCAL", os.path.join(BASE_DIR, "models", FILENAME))

CONF_THRES = float(os.environ.get("TOMATO_CONF", "0.45"))
IMG_SIZE = int(os.environ.get("TOMATO_IMGSZ", "416"))

app = Flask(__name__)
model = None

LABEL_MAP = {
    "unripe": ("green", "Quả xanh"),
    "green": ("green", "Quả xanh"),

    "semi-ripe": ("yellow", "Quả vàng"),
    "semi_ripe": ("yellow", "Quả vàng"),
    "semi ripe": ("yellow", "Quả vàng"),
    "half-ripe": ("yellow", "Quả vàng"),
    "half ripe": ("yellow", "Quả vàng"),
    "mild ripe": ("yellow", "Quả vàng"),
    "coloring": ("yellow", "Quả vàng"),
    "breaker": ("yellow", "Quả vàng"),
    "yellow": ("yellow", "Quả vàng"),

    "fully ripe": ("ripe", "Quả chín"),
    "ripe": ("ripe", "Quả chín"),
    "red": ("ripe", "Quả chín"),

    "rotten": ("defect", "Quả có vấn đề"),
    "defect": ("defect", "Quả có vấn đề"),
    "damaged": ("defect", "Quả có vấn đề"),
    "black": ("defect", "Quả có vấn đề"),
}


def safe_log(msg: str):
    line = f"[{datetime.now().strftime('%H:%M:%S')}] {msg}"
    try:
        print(line, flush=True)
    except Exception:
        pass

    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def safe_trace(prefix: str):
    tb = traceback.format_exc()
    safe_log(prefix)
    for line in tb.splitlines():
        safe_log(line)
    return tb


def pipe_fail(message: str, http_status: int = 200):
    # App C# parse dạng:
    # success|found|x|y|w|h|label|confidence|display
    # Để tránh app báo HTTP 500 HTML, lỗi server vẫn trả pipe text.
    return Response(
        f"0|0|0|0|0|0|none|0|{message}",
        status=http_status,
        mimetype="text/plain; charset=utf-8"
    )


@app.errorhandler(Exception)
def handle_any_exception(ex):
    tb = safe_trace("[FLASK_EXCEPTION]")
    return pipe_fail(f"SERVER_EXCEPTION:{type(ex).__name__}: {str(ex)}", http_status=200)


def ensure_model_file() -> str:
    global LOCAL_MODEL

    if not os.path.isabs(LOCAL_MODEL):
        LOCAL_MODEL = os.path.join(BASE_DIR, LOCAL_MODEL)

    if os.path.exists(LOCAL_MODEL):
        return LOCAL_MODEL

    model_dir = os.path.dirname(LOCAL_MODEL)
    if model_dir:
        os.makedirs(model_dir, exist_ok=True)

    safe_log(f"[MODEL] Local model not found, downloading {REPO_ID}/{FILENAME}")
    downloaded = hf_hub_download(repo_id=REPO_ID, filename=FILENAME)

    if downloaded and os.path.exists(downloaded):
        import shutil
        shutil.copyfile(downloaded, LOCAL_MODEL)
        return LOCAL_MODEL

    raise FileNotFoundError(f"Không tải được model {REPO_ID}/{FILENAME}")


def get_model() -> YOLO:
    global model

    if model is None:
        model_path = ensure_model_file()
        safe_log(f"[MODEL] Loading: {model_path}")
        model = YOLO(model_path)
        safe_log("[MODEL] Loaded OK")

    return model


def clamp_box(x1, y1, x2, y2, w, h):
    x1 = max(0, min(int(x1), w - 1))
    y1 = max(0, min(int(y1), h - 1))
    x2 = max(0, min(int(x2), w - 1))
    y2 = max(0, min(int(y2), h - 1))

    if x2 <= x1:
        x2 = min(w - 1, x1 + 1)
    if y2 <= y1:
        y2 = min(h - 1, y1 + 1)

    return x1, y1, x2, y2


def heuristic_defect(crop) -> Tuple[bool, float]:
    if crop is None or crop.size == 0:
        return False, 0.0

    try:
        hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
        h, s, v = cv2.split(hsv)
i
        # Chỉ xét vùng trung tâm crop. Nếu box của YOLO dính băng tải xanh đậm,
        # vùng ngoài crop sẽ không làm quả xanh bị ép thành defect.
        hh, ww = v.shape[:2]
        yy, xx = np.ogrid[:hh, :ww]
        cx, cy = ww / 2.0, hh / 2.0
        rx, ry = max(1.0, ww * 0.36), max(1.0, hh * 0.36)
        center_mask = (((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2) <= 1.0
        if np.count_nonzero(center_mask) < 10:
            center_mask = np.ones_like(v, dtype=bool)

        hc = h[center_mask]
        sc = s[center_mask]
        vc = v[center_mask]

        green_mask = (hc >= 39) & (hc <= 95) & (sc > 45) & (vc > 40)
        yellow_mask = (hc >= 13) & (hc <= 38) & (sc > 55) & (vc > 55)
        red_mask = (((hc <= 12) | (hc >= 165)) & (sc > 60) & (vc > 45))
        brown_mask = ((hc > 5) & (hc < 25) & (sc > 45) & (vc < 150))

        green_ratio = float(np.mean(green_mask))
        yellow_ratio = float(np.mean(yellow_mask))
        red_ratio = float(np.mean(red_mask))
        brown_ratio = float(np.mean(brown_mask))
        dark_ratio = float(np.mean(vc < 50))
        low_sat_dark = float(np.mean((vc < 65) & (sc < 75)))

        # Nếu màu quả đang rất rõ, đặc biệt là xanh, không ép sang defect chỉ vì nền tối.
        color_ratio = max(green_ratio, yellow_ratio, red_ratio)
        if green_ratio >= 0.16 and green_ratio >= brown_ratio * 0.9:
            return False, float(green_ratio)
        if color_ratio >= 0.22 and brown_ratio < 0.26 and dark_ratio < 0.42:
            return False, float(color_ratio)

        defect_score = max(dark_ratio, low_sat_dark * 0.9, brown_ratio * 1.25)
        return defect_score > 0.36, float(defect_score)
    except Exception:
        safe_trace("[DEFECT_HEURISTIC_EXCEPTION]")
        return False, 0.0


def normalize_label(raw_label: str, crop: np.ndarray):
    key = (raw_label or "").strip().lower().replace("_", "-")
    label, display = LABEL_MAP.get(key, ("unknown", raw_label or "Không xác định"))

    if label == "unknown":
        is_defect, score = heuristic_defect(crop)
        if is_defect:
            return "defect", "Quả có vấn đề", max(0.55, score)
        return "yellow", "Quả vàng", 0.40

    if label in ("green", "yellow", "ripe"):
        is_defect, score = heuristic_defect(crop)
        # Với nhãn đã nhận được là xanh/vàng/chín, chỉ đổi sang defect khi dấu hiệu hỏng rất mạnh.
        # Điều này tránh trường hợp băng tải xanh đậm/tối làm quả xanh bị nhận nhầm là lỗi.
        if is_defect and score >= 0.45:
            return "defect", "Quả có vấn đề", max(0.60, score)

    return label, display, None


def detect_tomato(frame: np.ndarray):
    m = get_model()

    results = m.predict(frame, imgsz=IMG_SIZE, conf=CONF_THRES, verbose=False)

    if not results:
        return False, 0, 0, 0, 0, "none", 0.0, "Không phát hiện quả cà chua"

    r = results[0]

    if r.boxes is None or len(r.boxes) == 0:
        return False, 0, 0, 0, 0, "none", 0.0, "Không phát hiện quả cà chua"

    boxes = r.boxes.xyxy.cpu().numpy()
    confs = r.boxes.conf.cpu().numpy()
    classes = r.boxes.cls.cpu().numpy().astype(int)
    names = r.names if isinstance(r.names, dict) else {}

    idx = int(np.argmax(confs))
    x1, y1, x2, y2 = boxes[idx].tolist()
    conf = float(confs[idx])
    cls_id = int(classes[idx])
    raw_label = str(names.get(cls_id, cls_id))

    h, w = frame.shape[:2]
    x1, y1, x2, y2 = clamp_box(x1, y1, x2, y2, w, h)
    crop = frame[y1:y2, x1:x2].copy()

    label, display, defect_conf = normalize_label(raw_label, crop)

    if defect_conf is not None:
        conf = max(conf, defect_conf)

    return True, x1, y1, x2 - x1, y2 - y1, label, conf, display


@app.route("/health", methods=["GET", "HEAD"])
def health():
    # Route này luôn trả 200 để app không tự kết luận server chết vì lỗi health phụ.
    try:
        model_path = ensure_model_file()
        exists = os.path.exists(model_path)
        text = (
            "ok"
            f"|model_exists={exists}"
            f"|model={model_path}"
            f"|python={sys.executable}"
            f"|cwd={os.getcwd()}"
        )
        safe_log("[HEALTH] " + text)
        return Response(text, status=200, mimetype="text/plain; charset=utf-8")
    except BaseException as ex:
        tb = safe_trace("[HEALTH_BASE_EXCEPTION]")
        return Response(
            f"ok|health_warning={type(ex).__name__}:{str(ex)}|log={LOG_FILE}",
            status=200,
            mimetype="text/plain; charset=utf-8"
        )


@app.route("/classify", methods=["POST"])
def classify():
    try:
        safe_log(f"[CLASSIFY] content_type={request.content_type}")

        if "image" not in request.files:
            safe_log("[CLASSIFY] missing image field")
            return pipe_fail("Thiếu file image", http_status=200)

        file = request.files["image"]
        raw = file.read()

        if not raw:
            safe_log("[CLASSIFY] empty image bytes")
            return pipe_fail("File ảnh rỗng", http_status=200)

        data = np.frombuffer(raw, dtype=np.uint8)
        safe_log(f"[CLASSIFY] bytes={len(data)} filename={getattr(file, 'filename', '')}")

        frame = cv2.imdecode(data, cv2.IMREAD_COLOR)

        if frame is None:
            safe_log("[CLASSIFY] decode failed")
            return pipe_fail("Không decode được ảnh", http_status=200)

        safe_log(f"[CLASSIFY] frame shape={frame.shape}")

        found, x, y, w, h, label, conf, display = detect_tomato(frame)

        safe_log(f"[CLASSIFY] found={found} label={label} conf={conf:.3f} box=({x},{y},{w},{h}) display={display}")

        return Response(
            f"1|{1 if found else 0}|{x}|{y}|{w}|{h}|{label}|{conf:.4f}|{display}",
            status=200,
            mimetype="text/plain; charset=utf-8"
        )

    except BaseException as ex:
        tb = safe_trace("[CLASSIFY_BASE_EXCEPTION]")
        # Trả 200 dạng pipe để C# đọc được lỗi, không trả HTML 500.
        short_msg = f"SERVER_EX:{type(ex).__name__}: {str(ex)}. Xem log: {LOG_FILE}"
        return pipe_fail(short_msg, http_status=200)


if __name__ == "__main__":
    safe_log("Tomato AI server starting...")
    safe_log("Model repo: " + REPO_ID)
    safe_log("Model file: " + FILENAME)
    safe_log("Base dir: " + BASE_DIR)
    safe_log("Debug log: " + LOG_FILE)

    try:
        ensure_model_file()
        get_model()
        safe_log("Model ready.")
    except BaseException:
        safe_trace("[STARTUP_BASE_EXCEPTION]")
        raise

    app.run(host=HOST, port=PORT, debug=False, use_reloader=False, threaded=True)
