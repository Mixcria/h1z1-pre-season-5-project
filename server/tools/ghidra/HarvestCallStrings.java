// Census script of the dev-console workflow (design lane, 2026-09-02). The run that produced
// the shipped data is logged in out\devconsole-20260901\ghidra\headless-*.log; its consolidated
// output, out\devconsole-20260901\ghidra\client-registry-1148.tsv, is the input of
// tools/devconsole/gen-client-registry.py -> src/Cranberry.Zone/DevConsole/ClientRegistry1148.g.cs.
//
// HarvestCallStrings — for each call site of the given functions (and their jump thunks), list the
// string literals referenced by the instructions just before the call. No decompiler: seconds, not
// minutes. Used to enumerate the client's console cvar/command registry names (design lane,
// dev-console workflow 2026-09-02).
//
//   analyzeHeadless <projectDir> <project> -process H1Z1.exe -noanalysis -readOnly
//       -scriptPath <dir> -postScript HarvestCallStrings.java <hexAddr[+hexAddr...]> <outFile>
//
// @category Cranberry

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.MemoryAccessException;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

public class HarvestCallStrings extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String[] targets = args[0].split("[+]");
        File out = new File(args[1]);
        if (out.getParentFile() != null) {
            out.getParentFile().mkdirs();
        }
        try (PrintWriter w = new PrintWriter(out)) {
            for (String t : targets) {
                Address a = toAddr(Long.parseLong(t, 16));
                Function f = getFunctionAt(a);
                if (f == null) {
                    w.println("# no function at " + t);
                    continue;
                }
                Set<Address> entries = new LinkedHashSet<>();
                entries.add(f.getEntryPoint());
                Address[] thunks = f.getFunctionThunkAddresses(true);
                if (thunks != null) {
                    for (Address th : thunks) {
                        entries.add(th);
                    }
                }
                w.println("# target " + f.getName() + " @ " + f.getEntryPoint()
                    + " thunks=" + (thunks == null ? 0 : thunks.length));
                int sites = 0;
                for (Address entry : entries) {
                    for (Reference ref : getReferencesTo(entry)) {
                        if (!ref.getReferenceType().isCall()) {
                            continue;
                        }
                        Address from = ref.getFromAddress();
                        Function owner = getFunctionContaining(from);
                        sites++;
                        List<String> near = new ArrayList<>();
                        List<String> far = new ArrayList<>();
                        Instruction ins = getInstructionAt(from);
                        int steps = 0;
                        boolean passedCall = false;
                        while (ins != null && steps < 30) {
                            if (owner != null && !owner.getBody().contains(ins.getAddress())) {
                                break;
                            }
                            if (steps > 0 && ins.getFlowType().isCall()) {
                                passedCall = true;
                            }
                            for (Reference r : ins.getReferencesFrom()) {
                                String s = stringAt(r.getToAddress());
                                if (s != null) {
                                    (passedCall ? far : near).add(s);
                                }
                            }
                            ins = ins.getPrevious();
                            steps++;
                        }
                        w.println(from + " in " + (owner == null ? "?" : owner.getName() + "@" + owner.getEntryPoint())
                            + " | near: " + String.join(" ; ", near) + " | far: " + String.join(" ; ", far));
                    }
                }
                w.println("# sites " + sites);
            }
        }
    }

    private String stringAt(Address addr) {
        Data d = getDataAt(addr);
        if (d != null && d.hasStringValue()) {
            Object v = d.getValue();
            return v == null ? null : "\"" + String.valueOf(v).replace("\n", "\n") + "\"";
        }
        try {
            byte[] bytes = new byte[96];
            int n = currentProgram.getMemory().getBytes(addr, bytes);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) {
                int b = bytes[i] & 0xff;
                if (b == 0) {
                    break;
                }
                if (b < 0x20 || b > 0x7e) {
                    return null;
                }
                sb.append((char) b);
            }
            if (sb.length() >= 2 && sb.length() < 96) {
                return "'" + sb + "'";
            }
        } catch (MemoryAccessException e) {
            return null;
        }
        return null;
    }
}
