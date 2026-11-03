-- La portée d'écriture des workers et groupes de workers s'appelle workers:write (elle s'appelait nodes:write).
-- Les portées sont stockées en texte séparé par des virgules.
UPDATE api_tokens SET scopes = replace(scopes, 'nodes:write', 'workers:write') WHERE scopes LIKE '%nodes:write%';
