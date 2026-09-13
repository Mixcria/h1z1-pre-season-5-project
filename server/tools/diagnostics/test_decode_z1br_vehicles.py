import struct
import unittest
from decode_z1br_vehicles import assemble, main_map_record, RC4


class OfflineCaptureTests(unittest.TestCase):
    def test_rc4_known_stream_vector(self):
        self.assertEqual('bbf316e8d940af0ad3', RC4(b'Key').apply(b'Plaintext').hex())

    def test_reordering_and_retransmission_deliver_each_fragment_once(self):
        def fragment(seq, payload):return b'\0\x0d'+seq.to_bytes(2,'big')+payload
        rows=[(1,1,'s2c',fragment(1,b'ef')),
              (2,2,'s2c',fragment(0,struct.pack('>I',6)+b'abcd')),
              (3,3,'s2c',fragment(1,b'ef'))]
        result, audit=assemble(rows,'s2c',0,0)
        self.assertEqual([b'abcdef'],[r[3] for r in result])
        self.assertEqual(1,audit['duplicates'])
        self.assertIsNone(audit['stopGap'])

    def test_missing_reliable_bytes_never_become_a_later_decoded_message(self):
        rows=[(1,1,'s2c',b'\0\x09\0\0abc'),(2,2,'s2c',b'\0\x09\0\2def')]
        result,audit=assemble(rows,'s2c',0,0)
        self.assertEqual([b'abc'],[r[3] for r in result])
        self.assertEqual(1,audit['stopGap'])

    def test_zone_and_playable_bounds_are_both_required(self):
        self.assertTrue(main_map_record({'zone':'Z2','position':[-524,237,-3721]}))
        self.assertFalse(main_map_record({'zone':'PracticeZone','position':[0,30,0]}))
        self.assertFalse(main_map_record({'zone':'Z2','position':[-400,500,-4900]}))


if __name__=='__main__':unittest.main()
