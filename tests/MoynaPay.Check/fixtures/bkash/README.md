# bKash golden fixtures

Every line is one real message, collected from a live personal account between July and
September 2026. Account numbers, transaction ids and names are replaced with values of
the same shape and length - the structure is what the parser reads, so changing the shape
would make the fixture useless.

Format: `EXPECTED_KIND<TAB>MESSAGE BODY`. Lines beginning with `#` are comments.
Multi-line messages are written with `\n` escapes.

Add to this file whenever a message arrives that the parser does not recognise. The
negatives are not filler: a parser that has never been shown an OTP is a parser that may
one day read one as a payment.

**Still missing, and it is the important one:** the message a merchant or retail account
receives when a customer pays it. Everything here is from a personal account. Capture it
on a real merchant handset during the T0A spike, before the pilot.
