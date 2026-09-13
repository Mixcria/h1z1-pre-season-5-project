import java.io.*;
import java.nio.file.*;
import java.util.*;
import com.jpexs.decompiler.flash.SWF;
import com.jpexs.decompiler.flash.abc.ABC;
import com.jpexs.decompiler.flash.abc.types.MethodBody;
import com.jpexs.decompiler.flash.abc.avm2.instructions.AVM2Instruction;

// Edit one existing method per GFx, retaining every original trait and namespace.
public class BinocularHudBytecode {
    static ABC abc;
    static AVM2Instruction op(int code, int... operands) {
        return new AVM2Instruction(0, code, operands.length == 0 ? null : operands);
    }
    static void require(boolean test, String message) {
        if (!test) throw new IllegalStateException(message);
    }
    static String name(AVM2Instruction instruction) {
        return abc.constants.getString(abc.constants.getMultiname(instruction.operands[0]).name_index);
    }
    static MethodBody method(String cls, String method) {
        MethodBody result = null;
        for (var instance : abc.instance_info) {
            if (!abc.constants.getString(abc.constants.getMultiname(instance.name_index).name_index).equals(cls)) continue;
            for (var trait : instance.instance_traits.traits) {
                if (trait instanceof com.jpexs.decompiler.flash.abc.types.traits.TraitMethodGetterSetter named &&
                    abc.constants.getString(abc.constants.getMultiname(trait.name_index).name_index).equals(method)) {
                    require(result == null, "Duplicate method " + method);
                    result = abc.findBody(named.method_info);
                }
            }
        }
        require(result != null, "Missing method " + cls + "." + method);
        return result;
    }
    public static void main(String[] args) throws Exception {
        SWF swf;
        try (InputStream in = Files.newInputStream(Path.of(args[0]))) { swf = new SWF(in, false); }
        require(swf.getAbcList().size() == 1, "Expected one ABC tag");
        abc = swf.getAbcList().get(0).getABC();
        Map<MethodBody, byte[]> originals = new IdentityHashMap<>();
        for (MethodBody body : abc.bodies) originals.put(body, body.getCodeBytes().clone());
        MethodBody changed;
        if (args[2].equals("ammo")) {
            changed = method("CurrentLoadoutRow", "weaponShouldShowAmmo");
            int sites = 0;
            for (int i = changed.getCode().code.size() - 1; i >= 0; i--) {
                var instruction = changed.getCode().code.get(i);
                if (instruction.definition.instructionCode == 0x46 && name(instruction).equals("GetData")) {
                    changed.insertAll(i + 1, List.of(op(0x2c, abc.constants.getStringId("1", true)), op(0xab)));
                    sites++;
                }
            }
            require(sites == 1, "Expected one ammo query");
            changed.max_stack += 1;
        } else throw new IllegalArgumentException("Expected ammo");
        changed.setModified();
        for (MethodBody body : abc.bodies)
            if (body != changed) require(Arrays.equals(originals.get(body), body.getCodeBytes()), "Unrelated method changed");
        System.out.println("CHANGED_METHOD=" + changed.method_info);
        System.out.println("UNCHANGED_METHODS=" + (abc.bodies.size() - 1));
        ((com.jpexs.decompiler.flash.tags.Tag)swf.getAbcList().get(0)).setModified(true);
        try (OutputStream out = Files.newOutputStream(Path.of(args[1]))) { swf.saveTo(out); }
    }
}
