-- Architecture 0045 : un nœud seul est autonome ; le cluster n'existe qu'une fois créé explicitement ou dès qu'un second nœud le rejoint.
ALTER TABLE cluster_identity ADD COLUMN established integer NOT NULL DEFAULT 0 CHECK (established IN (0, 1));
