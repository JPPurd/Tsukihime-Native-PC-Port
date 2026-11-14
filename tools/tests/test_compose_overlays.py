"""Tests for the overlay mark of tsuki.compose.execute_list (v0.1.9, the port's toast and achievement notice).

The port draws its overlays after a mark ("overlays", no PS4 record); the target is kept aside there, and the
next list ("frame") that reads the previous frame at its head (a capture before any picture, clear, load or
mark; fills do not end the head) gets that copy back first: a transition that starts while the notice is up
takes the frame without it. A frame without the mark runs as it always did. The GPU executor follows the same
rule (DisplayListExecutor; the Render tests compare the two).
"""
from __future__ import annotations

import numpy as np

from tsuki import compose as cp

W = H = 16
SCENE = [40, 90, 160, 255]
NOTICE = [240, 200, 30, 255]
NEXT = [10, 160, 60, 255]


def fill(colour, x=0, y=0, w=W, h=H, word=0):
    return {"op": "quad", "tex": None, "dst": [x, y, w, h], "src": [0, 0], "color": list(colour), "word": word}


def captured(surface, alpha=255):
    return {"op": "quad", "tex": f"capture:{surface}", "dst": [0, 0, W, H], "src": [0, 0],
            "color": [255, 255, 255, alpha], "word": 0}


SHOWN = [fill(SCENE), {"op": "overlays"}, fill(NOTICE, 10, 0, 6, 4)]       # the scene, the mark, the notice in its corner
START = [fill([255, 255, 255, 255], word=56), {"op": "capture", "surface": 0}]   # a transition start: the alpha fill, the capture


def run(ops):
    rt, caps = cp.execute_list(ops, {}, W, H)
    return rt, caps


def test_a_transition_start_after_the_notice_takes_the_frame_without_it() -> None:
    rt, caps = run(SHOWN + [{"op": "frame"}] + START + [fill(NEXT), captured(0)])
    assert rt[1, 12].tolist() == SCENE
    assert caps[0][1, 12].tolist() == SCENE
    assert rt[8, 8].tolist() == SCENE


def test_without_the_mark_the_capture_has_the_notice_as_always() -> None:
    rt, caps = run([fill(SCENE), fill(NOTICE, 10, 0, 6, 4), {"op": "frame"}] + START + [fill(NEXT), captured(0)])
    assert rt[1, 12].tolist() == NOTICE
    assert caps[0][1, 12].tolist() == NOTICE


def test_the_frame_as_shown_keeps_the_notice() -> None:
    rt, _ = run(SHOWN)
    assert rt[1, 12].tolist() == NOTICE
    rt, _ = run(SHOWN + [{"op": "frame"}])                                    # no list after it: nothing to read it
    assert rt[1, 12].tolist() == NOTICE


def test_a_list_that_draws_a_picture_first_gets_nothing_back() -> None:
    picture = {"op": "quad", "tex": "capture:1", "dst": [0, 8, W, 8], "src": [0, 8], "color": [255, 255, 255, 255], "word": 0}
    rt, _ = run(SHOWN + [{"op": "frame"}, picture, {"op": "capture", "surface": 0}])
    assert rt[1, 12].tolist() == NOTICE


def test_a_clear_or_a_load_forgets_the_kept_frame() -> None:
    rt, caps = run(SHOWN + [{"op": "clear", "rgba": [0, 0, 0, 255]}, {"op": "frame"}] + START)
    assert caps[0][1, 12].tolist() == [0, 0, 0, 255]
    black = np.zeros((H, W, 4), np.uint8)
    rt, caps = cp.execute_list(SHOWN + [{"op": "load", "tex": "B"}, {"op": "frame"}] + START, {"B": black}, W, H)
    assert caps[0][1, 12].tolist() == [0, 0, 0, 255]


def test_only_the_first_mark_of_a_list_counts_and_the_next_frame_forgets_it() -> None:
    rt, caps = run(SHOWN + [{"op": "overlays"}, fill(NEXT, 0, 12, 16, 4), {"op": "frame"}] + START)
    assert caps[0][1, 12].tolist() == SCENE
    assert caps[0][14, 2].tolist() == SCENE
    # the frame after the mark's frame is no transition start: the frame after that one has no copy to get back
    rt, caps = run(SHOWN + [{"op": "frame"}, fill(NEXT), {"op": "frame"}] + START)
    assert caps[0][1, 12].tolist() == NEXT
