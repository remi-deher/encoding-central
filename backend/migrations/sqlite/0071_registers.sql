-- Le registre clé/valeur du central (activation des plugins et des outils, points de reprise IA chiffrés) n'est plus
-- un registre de synchronisation : il prend un nom neutre et perd les colonnes de l'ancienne réplication (origin_id, sequence).
CREATE TABLE registers (
    register_key text PRIMARY KEY,
    value text NOT NULL CHECK (json_valid(value)),
    updated_at text NOT NULL
);
INSERT INTO registers(register_key, value, updated_at) SELECT register_key, value, occurred_at FROM cluster_sync_registers;
DROP TABLE cluster_sync_registers;
