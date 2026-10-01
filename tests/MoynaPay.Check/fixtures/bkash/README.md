# bKash golden fixtures

Every line is one real message, collected from a live personal account between July and
September 2026. Account numbers, transaction ids and names are replaced with values of
the same shape and length - the structure is what the parser reads, so changing the shape
would make the fixture useless.

Format:
`EXPECTED_KIND<TAB>MESSAGE BODY<TAB>AMOUNT<TAB>TRX_ID<TAB>OCCURRED_AT_UTC<TAB>COUNTERPARTY<TAB>REFERENCE<TAB>FEE<TAB>BALANCE_AFTER`.
Only the first two columns are required; later columns are asserted when present. Lines
beginning with `#` are comments. Multi-line messages are written with `\n` escapes.

Add to this file whenever a message arrives that the parser does not recognise. The
negatives are not filler: a parser that has never been shown an OTP is a parser that may
one day read one as a payment.

Merchant-payment wording is represented by a narrow seed row. Add every real
merchant-handset variant seen in the pilot before loosening that template.
