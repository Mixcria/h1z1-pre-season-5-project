// Dumps the August pellet spawn instructions and their constants for wire derivation.
// @category Cranberry
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.*;

public class DumpShotgunPattern extends GhidraScript {
    @Override public void run() throws Exception {
        File folder = new File(getScriptArgs()[0]);
        folder.mkdirs();
        try (PrintWriter output = new PrintWriter(new File(folder, "pellet-spawn.asm"))) {
            Function function = getFunctionAt(toAddr(0x140c7bb60L));
            InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
            while (instructions.hasNext()) {
                Instruction instruction = instructions.next();
                output.println(instruction.getAddress() + " " + instruction);
            }
            for (long address : new long[] {0x14311d670L, 0x1430fc92cL})
                output.println(Long.toHexString(address) + " float=" + Float.intBitsToFloat(getInt(toAddr(address))));
        }
    }
}
