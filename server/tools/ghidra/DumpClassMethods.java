// Dumps every symbol whose full name contains a needle, walks any vftable it finds (defining
// functions for slots that analysis missed), follows references to each vftable (the class's
// constructors) and their callers (factories / dispatchers), and decompiles every function
// reached, one .c file per function. Also decompiles every function that references a string
// containing the needle.
//
// Headless use (read-only, no analysis):
//   analyzeHeadless <projectDir> <project> -process H1Z1.exe -noanalysis -readOnly
//       -scriptPath C:\Aug2017\Server\tools\ghidra -postScript DumpClassMethods.java <needle> <outDir> [depth]
// Prefix an address list with '@' to walk callees, 'callers:' to walk callers, or
// 'refs:' to find and decompile code that references an arbitrary code/data address.
//
// @category Cranberry

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolType;
import ghidra.program.model.symbol.SymbolTable;

import java.io.File;
import java.io.IOException;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;

public class DumpClassMethods extends GhidraScript {

    private DecompInterface decompiler;
    private final Set<Function> dumped = new HashSet<>();
    private File outDir;
    private int callerDepth = 1;

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String needle = args.length > 0 ? args[0] : "PacketLoginReply";
        String folder = needle.replaceAll("[^A-Za-z0-9_]+", "_");
        outDir = new File(args.length > 1 ? args[1] : "C:\\Aug2017\\out\\ghidra", folder);
        outDir.mkdirs();
        if (args.length > 2) {
            callerDepth = Integer.parseInt(args[2]);
        }

        decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        try (PrintWriter index = new PrintWriter(new File(outDir, "index.txt"))) {
            if (needle.startsWith("scalar:")) {
                String[] parts = needle.split(":");
                long wanted = Long.parseLong(parts[1], 16);
                Address start = toAddr(parts.length > 2 ? Long.parseLong(parts[2], 16) : 0x140000000L);
                Address end = toAddr(parts.length > 3 ? Long.parseLong(parts[3], 16) : 0x146000000L);
                index.println("# functions using scalar 0x" + Long.toHexString(wanted)
                    + " from " + start + " through " + end);
                Set<Function> matches = new HashSet<>();
                InstructionIterator instructions = currentProgram.getListing()
                    .getInstructions(new AddressSet(start, end), true);
                while (instructions.hasNext() && !monitor.isCancelled()) {
                    Instruction instruction = instructions.next();
                    for (int operand = 0; operand < instruction.getNumOperands(); operand++) {
                        for (Object object : instruction.getOpObjects(operand)) {
                            if (object instanceof Scalar
                                && ((Scalar) object).getUnsignedValue() == wanted) {
                                Function function = getFunctionContaining(instruction.getAddress());
                                if (function != null && matches.add(function)) {
                                    index.println("    " + instruction.getAddress() + " in "
                                        + function.getEntryPoint() + "  " + function.getName(true));
                                    dump(function, index);
                                }
                            }
                        }
                    }
                }
                decompiler.dispose();
                println("wrote " + dumped.size() + " function(s) to " + outDir);
                return;
            }

            if (needle.startsWith("refs:")) {
                index.println("# references to " + needle.substring("refs:".length())
                    + " in " + currentProgram.getName());
                for (String hex : needle.substring("refs:".length()).split("[,+]")) {
                    Address target = toAddr(Long.parseLong(hex.trim(), 16));
                    Symbol symbol = currentProgram.getSymbolTable().getPrimarySymbol(target);
                    index.println(target + (symbol == null ? "" : "  " + symbol.getName(true)));
                    index.println("    nearby pointer-sized slots:");
                    for (int i = -8; i <= 8; i++) {
                        Address slot = target.add(i * 8L);
                        try {
                            Address value = toAddr(currentProgram.getMemory().getLong(slot));
                            Symbol slotSymbol = currentProgram.getSymbolTable().getPrimarySymbol(slot);
                            Symbol valueSymbol = currentProgram.getSymbolTable().getPrimarySymbol(value);
                            index.println("      " + slot + " -> " + value
                                + (slotSymbol == null ? "" : "  slot=" + slotSymbol.getName(true))
                                + (valueSymbol == null ? "" : "  value=" + valueSymbol.getName(true)));
                        } catch (Exception ignored) {
                            index.println("      " + slot + " -> (unreadable)");
                        }
                    }
                    for (Reference ref : getReferencesTo(target)) {
                        Function owner = getFunctionContaining(ref.getFromAddress());
                        index.println("    " + ref.getReferenceType() + " from " + ref.getFromAddress()
                            + (owner == null ? "" : " in " + owner.getName(true)));
                        if (owner != null) {
                            dump(owner, index);
                            dumpCallers(owner, index, callerDepth, "      ");
                        }
                    }
                }
                decompiler.dispose();
                println("wrote " + dumped.size() + " function(s) to " + outDir);
                return;
            }

            if (needle.startsWith("@") || needle.startsWith("callers:")) {
                // Address-list mode: "@141558300+14154d530" decompiles those functions and
                // their callees; 'callers:' performs the corresponding caller walk.
                boolean walkCallers = needle.startsWith("callers:");
                index.println("# functions " + needle + " in " + currentProgram.getName());
                // '+' separated: the headless launcher splits arguments on commas.
                String addresses = walkCallers ? needle.substring("callers:".length()) : needle.substring(1);
                for (String hex : addresses.split("[,+]")) {
                    Function f = functionOrCreate(toAddr(Long.parseLong(hex.trim(), 16)));
                    if (f == null) {
                        index.println(hex + "  not a function");
                        continue;
                    }
                    if (f.isThunk() && f.getThunkedFunction(true) != null) {
                        index.println(hex + "  is a thunk for " + f.getThunkedFunction(true).getEntryPoint());
                        f = f.getThunkedFunction(true);
                    }
                    index.println(f.getEntryPoint() + "  " + f.getName(true));
                    dump(f, index);
                    if (walkCallers) {
                        dumpCallers(f, index, callerDepth, "    ");
                    } else {
                        dumpCallees(f, index, callerDepth, "    ");
                    }
                }
                decompiler.dispose();
                println("wrote " + dumped.size() + " function(s) to " + outDir);
                return;
            }

            index.println("# symbols containing '" + needle + "' in " + currentProgram.getName());
            SymbolTable table = currentProgram.getSymbolTable();
            for (Symbol symbol : table.getAllSymbols(true)) {
                if (monitor.isCancelled()) {
                    break;
                }
                String full = symbol.getName(true);
                if (!full.contains(needle)) {
                    continue;
                }
                index.println(symbol.getAddress() + "  " + symbol.getSymbolType() + "  " + full);

                String name = symbol.getName();
                if (name.equals("vftable") || name.contains("vtable")) {
                    walkVtable(symbol.getAddress(), index);
                    followConstructors(symbol.getAddress(), index);
                }
                if (symbol.getSymbolType() == SymbolType.FUNCTION) {
                    Function f = getFunctionAt(symbol.getAddress());
                    if (f != null) {
                        dump(f, index);
                    }
                }
            }

            index.println();
            index.println("# functions referencing strings containing '" + needle + "'");
            DataIterator data = currentProgram.getListing().getDefinedData(true);
            while (data.hasNext() && !monitor.isCancelled()) {
                Data d = data.next();
                Object value = d.getValue();
                if (!(value instanceof String) || !((String) value).contains(needle)) {
                    continue;
                }
                index.println(d.getAddress() + "  string  \"" + value + "\"");
                for (Reference ref : getReferencesTo(d.getAddress())) {
                    Function f = getFunctionContaining(ref.getFromAddress());
                    if (f != null) {
                        index.println("    used by " + f.getEntryPoint() + "  " + f.getName(true));
                        dump(f, index);
                    }
                }
            }
        }
        decompiler.dispose();
        println("wrote " + dumped.size() + " function(s) to " + outDir);
    }

    private boolean isCode(Address a) {
        MemoryBlock block = currentProgram.getMemory().getBlock(a);
        return block != null && block.isExecute();
    }

    private Function functionOrCreate(Address a) {
        Function f = getFunctionContaining(a);
        if (f != null || !isCode(a)) {
            return f;
        }
        try {
            disassemble(a);
            return createFunction(a, null);
        } catch (Exception e) {
            return null;
        }
    }

    private void walkVtable(Address vtable, PrintWriter index) throws Exception {
        for (int i = 0; i < 96; i++) {
            Address slot = vtable.add(i * 8L);
            long value;
            try {
                value = currentProgram.getMemory().getLong(slot);
            } catch (Exception e) {
                break;
            }
            Address target = toAddr(value);
            if (!isCode(target)) {
                break;
            }
            // A slot that is itself referenced as data (the next class's meta pointer) ends this table.
            if (i > 0 && currentProgram.getSymbolTable().getPrimarySymbol(slot) != null) {
                break;
            }
            Function f = functionOrCreate(target);
            if (f == null) {
                index.println("    vt[" + i + "] " + target + "  (not a function)");
                continue;
            }
            index.println("    vt[" + i + "] " + f.getEntryPoint() + "  " + f.getName(true));
            dump(f, index);
        }
    }

    private void followConstructors(Address vtable, PrintWriter index) throws IOException {
        for (Reference ref : getReferencesTo(vtable)) {
            Function ctor = getFunctionContaining(ref.getFromAddress());
            if (ctor == null) {
                continue;
            }
            index.println("    vftable stored by " + ctor.getEntryPoint() + "  " + ctor.getName(true));
            dump(ctor, index);
            dumpCallers(ctor, index, callerDepth, "      ");
        }
    }

    private void dumpCallees(Function caller, PrintWriter index, int depth, String indent) throws IOException {
        if (depth <= 0) {
            return;
        }
        for (Function callee : caller.getCalledFunctions(monitor)) {
            if (callee.isExternal()) {
                continue;
            }
            // Follow jump thunks to the code they stand for.
            Function target = callee.isThunk() ? callee.getThunkedFunction(true) : callee;
            if (target == null || target.isExternal()) {
                continue;
            }
            index.println(indent + "calls " + target.getEntryPoint() + "  " + target.getName(true)
                + (callee.isThunk() ? "  (via thunk " + callee.getEntryPoint() + ")" : ""));
            dump(target, index);
            dumpCallees(target, index, depth - 1, indent + "  ");
        }
    }

    private void dumpCallers(Function callee, PrintWriter index, int depth, String indent) throws IOException {
        if (depth <= 0) {
            return;
        }
        for (Reference ref : getReferencesTo(callee.getEntryPoint())) {
            Function owner = getFunctionContaining(ref.getFromAddress());
            if (owner == null) {
                Symbol symbol = currentProgram.getSymbolTable().getPrimarySymbol(ref.getFromAddress());
                index.println(indent + "referenced as data from " + ref.getFromAddress()
                    + (symbol == null ? "" : "  " + symbol.getName(true)));
            }
        }
        for (Function caller : callee.getCallingFunctions(monitor)) {
            index.println(indent + "called from " + caller.getEntryPoint() + "  " + caller.getName(true));
            dump(caller, index);
            dumpCallers(caller, index, depth - 1, indent + "  ");
        }
        // Incrementally linked builds reach functions through jump thunks; a thunk's callers are
        // the real callers.
        Address[] thunks = callee.getFunctionThunkAddresses(true);
        if (thunks == null) {
            return;
        }
        for (Address thunkAddress : thunks) {
            Function thunk = getFunctionAt(thunkAddress);
            if (thunk == null) {
                continue;
            }
            index.println(indent + "via thunk " + thunkAddress);
            for (Function caller : thunk.getCallingFunctions(monitor)) {
                index.println(indent + "  called from " + caller.getEntryPoint() + "  " + caller.getName(true));
                dump(caller, index);
                dumpCallers(caller, index, depth - 1, indent + "    ");
            }
        }
    }

    private void dump(Function f, PrintWriter index) throws IOException {
        if (!dumped.add(f)) {
            return;
        }
        DecompileResults results = decompiler.decompileFunction(f, 90, monitor);
        String text = results.getDecompiledFunction() != null
            ? results.getDecompiledFunction().getC()
            : "// decompile failed: " + results.getErrorMessage();
        String fileName = f.getName().replaceAll("[^A-Za-z0-9_]", "_") + "_" + f.getEntryPoint() + ".c";
        try (PrintWriter w = new PrintWriter(new File(outDir, fileName))) {
            w.println("// " + f.getName(true) + " @ " + f.getEntryPoint() + "  (" + currentProgram.getName() + ")");
            w.print(text);
        }
    }
}
