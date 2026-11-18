# NotebookLM provenance fixture

The binary fixture contains only the original UUID box from How_AI_Teaches_Itself_in_Generated_Worlds.m4a, inspected on 2026-11-18. It contains no audio. It is used inside synthetic MP4 containers in Mp4ParserTests, not as a complete playable file.

The active manifest records opening and transcoding a parent asset. Its RFC 3161 genTime is 2026-11-17T17:10:55Z. The parent manifest records a creation action and has genTime 2026-11-17T17:09:37Z. The parent signing certificate identifies its subject as NotebookLM. The creation action itself does not name a software agent. Both timestamp message imprints match their COSE CounterSignature structures. The expected date is the RFC 3161 genTime associated with the parent manifest's claim signature.

The parser tests use this fixture to exercise metadata extraction, local hashed-reference consistency, and the match between the timestamp message imprint and the encoded COSE CounterSignature structure. It covers the NotebookLM export layout observed in this file. The reader does not validate general C2PA compliance, certificates, signatures or the audio content binding. The fixture itself omits the media those bindings describe.
