-- Un worker ne fonctionne qu'avec la version exacte du central : la version de l'application remplace le numéro de protocole.
-- Les raisons d'incompatibilité calculées avec l'ancien protocole sont recalculées au prochain signal de présence.
ALTER TABLE cluster_nodes ADD COLUMN app_version TEXT;
ALTER TABLE cluster_nodes DROP COLUMN protocol_version;
UPDATE cluster_nodes SET incompatible_reason=NULL;
