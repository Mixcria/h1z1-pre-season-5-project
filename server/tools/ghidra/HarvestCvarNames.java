// Census script of the dev-console workflow (design lane, 2026-09-02). The run that produced
// the shipped data is logged in out\devconsole-20260901\ghidra\headless-*.log; its consolidated
// output, out\devconsole-20260901\ghidra\client-registry-1148.tsv, is the input of
// tools/devconsole/gen-client-registry.py -> src/Cranberry.Zone/DevConsole/ClientRegistry1148.g.cs.
//
// HarvestCvarNames - second pass over the registry objects found by HarvestCvarObjects (LEA RDX
// before a FUN_141e9baa0 call). For each object it reads hash (+0x38) and name pointer (+0x28)
// from the image; when the name pointer is null in the image it looks for the code that stores
// obj+0x28 (MOV [obj+0x28],reg fed by LEA reg,[string]) and reads that string, and likewise for
// the code that stores obj+0x38 (MOV dword [obj+0x38],imm32) to recover the runtime hash.
// Output TSV: site, owner, object, hash, name, writer.
//
//   analyzeHeadless <projectDir> <project> -process H1Z1.exe -noanalysis -readOnly
//       -scriptPath <dir> -postScript HarvestCvarNames.java <hexAddrOfInsert> <outFile>
//
// @category Cranberry

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryAccessException;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class HarvestCvarNames extends GhidraScript {

    private Memory mem;

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Address target = toAddr(Long.parseLong(args[0], 16));
        File out = new File(args[1]);
        Function f = getFunctionAt(target);
        mem = currentProgram.getMemory();
        Set<Address> entries = new LinkedHashSet<>();
        entries.add(f.getEntryPoint());
        Address[] thunks = f.getFunctionThunkAddresses(true);
        if (thunks != null) {
            for (Address th : thunks) {
                entries.add(th);
            }
        }
        try (PrintWriter w = new PrintWriter(out)) {
            w.println("# site\towner\tobject\thash\tname\twriter");
            for (Address entry : entries) {
                for (Reference ref : getReferencesTo(entry)) {
                    if (!ref.getReferenceType().isCall()) {
                        continue;
                    }
                    Address from = ref.getFromAddress();
                    Function owner = getFunctionContaining(from);
                    Address obj = findRdxObject(from, owner);
                    String hash = "";
                    String name = "<no-rdx>";
                    String writer = "";
                    if (obj != null) {
                        try {
                            int imageHash = mem.getInt(obj.add(0x38));
                            hash = String.format("image:0x%08x", imageHash);
                            String runtimeHash = findImmediateStore(obj.add(0x38));
                            if (runtimeHash != null) {
                                hash = "code:" + runtimeHash;
                            }
                            long namePtr = mem.getLong(obj.add(0x28));
                            if (namePtr != 0) {
                                name = readAscii(toAddr(namePtr));
                                writer = "image";
                            } else {
                                name = "<runtime>";
                                for (Reference r : getReferencesTo(obj.add(0x28))) {
                                    Instruction st = getInstructionAt(r.getFromAddress());
                                    if (st == null || !st.getMnemonicString().equals("MOV") || st.getNumOperands() != 2) {
                                        continue;
                                    }
                                    Object[] src = st.getOpObjects(1);
                                    if (src.length != 1) {
                                        continue;
                                    }
                                    String reg = src[0].toString();
                                    Instruction p = st;
                                    for (int i = 0; i < 12 && p != null; i++) {
                                        p = p.getPrevious();
                                        if (p == null) {
                                            break;
                                        }
                                        Object[] d = p.getOpObjects(0);
                                        if (d.length == 1 && d[0].toString().equals(reg)
                                            && (p.getMnemonicString().equals("LEA") || p.getMnemonicString().equals("MOV"))) {
                                            Address s = null;
                                            for (Reference rr : p.getReferencesFrom()) {
                                                s = rr.getToAddress();
                                            }
                                            if (s != null) {
                                                name = readAscii(s);
                                                writer = st.getAddress().toString();
                                            }
                                            break;
                                        }
                                    }
                                    if (writer.length() > 0) {
                                        break;
                                    }
                                }
                            }
                        } catch (MemoryAccessException e) {
                            name = "<unmapped>";
                        }
                    }
                    w.println(from + "\t" + (owner == null ? "?" : owner.getName()) + "\t" + obj + "\t" + hash + "\t" + name + "\t" + writer);
                }
            }
        }
    }

    // MOV dword ptr [addr], imm32 -> the immediate, else null.
    private String findImmediateStore(Address addr) {
        for (Reference r : getReferencesTo(addr)) {
            Instruction st = getInstructionAt(r.getFromAddress());
            if (st == null || !st.getMnemonicString().equals("MOV") || st.getNumOperands() != 2) {
                continue;
            }
            Object[] src = st.getOpObjects(1);
            if (src.length == 1 && src[0] instanceof Scalar) {
                return String.format("0x%08x", ((Scalar) src[0]).getUnsignedValue());
            }
        }
        return null;
    }

    private Address findRdxObject(Address from, Function owner) {
        Instruction ins = getInstructionAt(from);
        for (int steps = 0; ins != null && steps < 16; steps++) {
            ins = ins.getPrevious();
            if (ins == null || (owner != null && !owner.getBody().contains(ins.getAddress()))) {
                return null;
            }
            String mn = ins.getMnemonicString();
            if ((mn.equals("LEA") || mn.equals("MOV")) && ins.getNumOperands() == 2) {
                Object[] op0 = ins.getOpObjects(0);
                if (op0.length == 1 && op0[0].toString().equals("RDX")) {
                    Address obj = null;
                    for (Reference r : ins.getReferencesFrom()) {
                        obj = r.getToAddress();
                    }
                    return obj;
                }
            }
        }
        return null;
    }

    private String readAscii(Address addr) {
        try {
            byte[] bytes = new byte[128];
            int n = mem.getBytes(addr, bytes);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) {
                int b = bytes[i] & 0xff;
                if (b == 0) {
                    return sb.toString();
                }
                if (b < 0x20 || b > 0x7e) {
                    return "<binary@" + addr + ">";
                }
                sb.append((char) b);
            }
            return sb.toString() + "...";
        } catch (MemoryAccessException e) {
            return "<unmapped@" + addr + ">";
        }
    }
}
