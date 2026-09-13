"""Native opcode/boolean semantics and rapid repeated F attempts, without a client."""
import hashlib
import importlib.util
from pathlib import Path
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "interaction_throttle_evidence.py"
SPEC = importlib.util.spec_from_file_location("throttle_evidence", SOURCE)
THROTTLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(THROTTLE)


def native_result(now, next_time, instruction):
    # 14158cda6..cdbd clamps the signed millisecond difference to int32,
    # 14158cdc0 shifts the sign bit into R14D, then the reviewed instruction
    # produces the returned boolean and controls the native bookkeeping branch.
    delta = max(-(1 << 31), min((1 << 31) - 1, now - next_time))
    value = (delta & 0xffffffff) >> 31
    if instruction == b"\x41\x80\xf6\x01":
        return value ^ 1
    if instruction == b"\x41\x80\xce\x01":
        return value | 1
    raise ValueError("Unexpected instruction")


class InteractionThrottleEvidenceTests(unittest.TestCase):
    def test_only_reviewed_xor_to_or_byte_changes(self):
        old, new = THROTTLE.ORIGINAL_FUNCTION, THROTTLE.PATCHED_FUNCTION
        self.assertEqual(283, len(old))
        self.assertEqual("072cc6ddb6f80d14f6817dbd1b95e56cfa1d90858da7266e301351c073f133d4",
                         hashlib.sha256(old).hexdigest())
        self.assertEqual([THROTTLE.PATCH_INDEX], [i for i, pair in enumerate(zip(old, new)) if pair[0] != pair[1]])
        index = THROTTLE.PATCH_INDEX
        self.assertEqual(b"\x41\x80\xf6\x01", old[index - 2:index + 2])
        self.assertEqual(b"\x41\x80\xce\x01", new[index - 2:index + 2])
        # Shift input to 0/1, bookkeeping branch and checksum epilogue unchanged.
        self.assertEqual(b"\x41\xc1\xee\x1f", new[index - 6:index - 2])
        self.assertEqual(b"\x74\x43", new[index + 2:index + 4])
        self.assertEqual(old[index + 1:], new[index + 1:])

    def test_patched_result_is_exactly_true_for_future_current_and_expired_deadline(self):
        for delta in (-(1 << 40), -1024, -64, -1, 0, 1, 1024, 1 << 40):
            self.assertEqual(int(delta >= 0), native_result(delta, 0, b"\x41\x80\xf6\x01"))
            self.assertEqual(1, native_result(delta, 0, b"\x41\x80\xce\x01"))

    def test_rapid_attempts_remain_admitted_after_original_throttle_reaches_one_second(self):
        def admitted(instruction):
            delay, next_time, accepted = 64, 0, []
            for now in range(0, 3001, 50):
                if native_result(now, next_time, instruction):
                    accepted.append(now)
                    delay = min(delay * 2, 1024)
                    next_time = now + delay
            return accepted
        self.assertEqual([0, 150, 450, 1000, 2050], admitted(b"\x41\x80\xf6\x01"))
        self.assertEqual(list(range(0, 3001, 50)), admitted(b"\x41\x80\xce\x01"))

    def test_unknown_throttle_function_is_rejected(self):
        data = bytearray(THROTTLE.ORIGINAL_FUNCTION)
        data[-1] ^= 1
        with self.assertRaisesRegex(ValueError, "283-byte"):
            THROTTLE.function_state(data)


if __name__ == "__main__":
    unittest.main()
