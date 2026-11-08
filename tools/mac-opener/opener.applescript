-- 3MF Library Opener: handles threemflib:// links from the library site by opening the local copy in Bambu Studio.
-- The work (decoding, checks, opening) is done by open-model.sh inside the app's Resources.

on open location theURL
	try
		do shell script quoted form of (POSIX path of (path to resource "open-model.sh")) & " " & quoted form of theURL
	on error message
		display dialog message buttons {"OK"} default button 1 with title "3MF Library Opener" with icon caution
	end try
end open location

on run
	set libraryFolder to read (path to resource "library-folder.txt") as «class utf8»
	display dialog "This helper opens models from the library site in Bambu Studio, using your local copy in:" & return & return & libraryFolder & return & return & "Turn it on per computer under Account → This computer on the site. Nothing to do here." buttons {"OK"} default button 1 with title "3MF Library Opener"
end run
