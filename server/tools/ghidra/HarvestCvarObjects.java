// Census script of the dev-console workflow (design lane, 2026-09-02). The run that produced
// the shipped data is logged in out\devconsole-20260901\ghidra\headless-*.log; its consolidated
// output, out\devconsole-20260901\ghidra\client-registry-1148.tsv, is the input of
// tools/devconsole/gen-client-registry.py -> src/Cranberry.Zone/DevConsole/ClientRegistry1148.g.cs.
//
// HarvestCvarObjects - enumerate the client's console CVar/command registry from the binary.
// Every registration goes through FUN_141e9baa0(flag, obj, extra); the object carries its name at
// obj+0x28 (char*) and its hash at obj+0x38 (u32), both statically initialised for the ~1,000
// global CVars registered by dynamic initialisers. For each call site this script finds the
// LEA RDX,[obj] that feeds the call and reads name + hash out of the program image.
// Objects built at runtime (FUN_141277a50's built-in table, aliases) show up as name=<runtime>.
//
//   analyzeHeadless <projectDir> <project> -process H1Z1.exe -noanalysis -readOnly
//       -scriptPath <dir> -postScript HarvestCvarObjects.java <hexAddrOfInsert> <outFile>
//
// @category Cranberry

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryAccessException;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class HarvestCvarObjects extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Address target = toAddr(Long.parseLong(args[0], 16));
        File out = new File(args[1]);
        Function f = getFunctionAt(target);
        Memory mem = currentProgram.getMemory();
        Set<Address> entries = new LinkedHashSet<>();
        entries.add(f.getEntryPoint());
        Address[] thunks = f.getFunctionThunkAddresses(true);
        if (thunks != null) {
            for (Address th : thunks) {
                entries.add(th);
            }
        }
        try (PrintWriter w = new PrintWriter(out)) {
            w.println("# site\towner\tobject\thash\tname");
            int sites = 0;
            for (Address entry : entries) {
                for (Reference ref : getReferencesTo(entry)) {
                    if (!ref.getReferenceType().isCall()) {
                        continue;
                    }
                    sites++;
                    Address from = ref.getFromAddress();
                    Function owner = getFunctionContaining(from);
                    Address obj = null;
                    Instruction ins = getInstructionAt(from);
                    int steps = 0;
                    while (ins != null && steps < 16 && obj == null) {
                        ins = ins.getPrevious();
                        steps++;
                        if (ins == null) {
                            break;
                        }
                        if (owner != null && !owner.getBody().contains(ins.getAddress())) {
                            break;
                        }
                        String mn = ins.getMnemonicString();
                        if ((mn.equals("LEA") || mn.equals("MOV")) && ins.getNumOperands() == 2) {
                            Object[] op0 = ins.getOpObjects(0);
                            if (op0.length == 1 && op0[0].toString().equals("RDX")) {
                                for (Reference r : ins.getReferencesFrom()) {
                                    obj = r.getToAddress();
                                }
                                if (obj == null) {
                                    break; // RDX set from something we cannot read statically
                                }
                            }
                        }
                    }
                    String name = "<no-rdx>";
                    String hash = "";
                    if (obj != null) {
                        try {
                            long namePtr = mem.getLong(obj.add(0x28));
                            int h = mem.getInt(obj.add(0x38));
                            hash = String.format("0x%08x", h);
                            if (namePtr == 0) {
                                name = "<runtime>";
                            } else {
                                name = readAscii(mem, toAddr(namePtr));
                            }
                        } catch (MemoryAccessException e) {
                            name = "<unmapped " + obj + ">";
                        }
                    }
                    w.println(from + "\t" + (owner == null ? "?" : owner.getName()) + "\t" + obj + "\t" + hash + "\t" + name);
                }
            }
            w.println("# sites " + sites);
        }
    }

    private String readAscii(Memory mem, Address addr) {
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
