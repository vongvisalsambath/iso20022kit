package conformance;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.prowidesoftware.swift.model.mx.AbstractMX;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Runs the conformance suite against Prowide ISO 20022 (open-source edition).
 * The open-source edition parses messages but has no schema or rule validation (that is in Prowide's
 * commercial SDK), so a case counts as "valid" when AbstractMX.parse returns a message.
 * For the security cases it also reports whether an entity was expanded into the parsed content.
 * Usage: java -cp target/classes:target/lib/* conformance.Runner ../../suite.json
 */
public class Runner {
    public static void main(String[] args) throws Exception {
        Path suitePath = Path.of(args[0]);
        Gson gson = new Gson();
        JsonObject suite = gson.fromJson(Files.readString(suitePath), JsonObject.class);
        // Prowide logs parse problems through java.util.logging; keep stdout clean for the results
        java.util.logging.LogManager.getLogManager().reset();
        for (var element : suite.getAsJsonArray("cases")) {
            JsonObject c = element.getAsJsonObject();
            String id = c.get("id").getAsString();
            String xml = Files.readString(suitePath.getParent().resolve(c.get("file").getAsString()));
            Map<String, String> out = new LinkedHashMap<>();
            out.put("id", id);
            try {
                AbstractMX mx = AbstractMX.parse(xml);
                if (mx == null) {
                    out.put("result", "invalid");
                    out.put("detail", "AbstractMX.parse returned null");
                } else {
                    out.put("result", "valid");
                    String content = mx.message();
                    if (content.contains("root:") || content.contains("aaaaaaaaaaaaaaaaaaaa")) {
                        out.put("detail", "ENTITY EXPANDED into the parsed message (" + content.length() + " chars)");
                    } else {
                        out.put("detail", "parsed as " + mx.getMxId().id());
                    }
                }
            } catch (Throwable t) {
                out.put("result", "invalid");
                String msg = String.valueOf(t);
                out.put("detail", msg.length() > 300 ? msg.substring(0, 300) + "…" : msg);
            }
            System.out.println(gson.toJson(out));
        }
    }
}
