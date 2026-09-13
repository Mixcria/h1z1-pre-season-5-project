"""Reviewed August F-only exponential interaction throttle; no process access.

FUN_14158cd30's sole caller is FUN_14158e700 at 14158ea81, through thunk
14005b23a. R14D is the sign bit (0 or 1) of clamped now-nextAllowedTime.
Changing XOR R14B,1 to OR R14B,1 always permits this F attempt, while leaving
the native timing fields and their guarded checksum updates consistent.
"""
import hashlib

FUNCTION_RVA = 0x158CD30
PATCH_RVA = 0x158CDC6
PATCH_INDEX = PATCH_RVA - FUNCTION_RVA
ORIGINAL_FUNCTION = bytes.fromhex("""
48895c241048896c2418565741564883ec20488db99000000033ed8b8f880200008d7550
8d410189878802000085c9752c488bc78bd58bce0f1f840000000000483310488d4008
4883e90175f3483b97800200007407c6878c02000001488b9f68020000488d4c2440e8
a8fdb4fe488b08b8ffffff7f482bcb483bc8480f4cc148c7c100000080483bc10f4cc1
448bf041c1ee1f4180f60174438b8f700200008bd503c981f9000400000f9ec2ffca8b
c281e200040000f7d023c1488d4c24400bc28987700200004863d8e846fdb4fe488b00
4803c348898768020000ff8f8802000039af880200007f19488bcf6690483329488d4908
4883ee0175f34889af80020000488b5c2448410fb6c6488b6c24504883c420415e5f5ec3
""")
ORIGINAL_SHA256 = "072cc6ddb6f80d14f6817dbd1b95e56cfa1d90858da7266e301351c073f133d4"
assert len(ORIGINAL_FUNCTION) == 283
assert hashlib.sha256(ORIGINAL_FUNCTION).hexdigest() == ORIGINAL_SHA256
assert ORIGINAL_FUNCTION[PATCH_INDEX - 2:PATCH_INDEX + 2] == b"\x41\x80\xf6\x01"
PATCHED_FUNCTION = ORIGINAL_FUNCTION[:PATCH_INDEX] + b"\xce" + ORIGINAL_FUNCTION[PATCH_INDEX + 1:]


def function_state(data):
    if data == ORIGINAL_FUNCTION:
        return "original"
    if data == PATCHED_FUNCTION:
        return "patched"
    raise ValueError("Full 283-byte F throttle function differs from reviewed bytes; refusing")
