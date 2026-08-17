$ErrorActionPreference = 'Stop'
$all = Get-Content -Raw (Join-Path $PSScriptRoot 'media-library.json') | ConvertFrom-Json
$outDir = Join-Path $PSScriptRoot 'sheet-data'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Fix-Title($t) {
  if ($t -match '^Disclosure Day') { return 'Disclosure Day' }
  if ($t -match '^Of Mice and Men') { return 'Of Mice and Men' }
  if ($t -eq 'Rambo First Blood') { return 'First Blood' }
  if ($t -eq 'Rambo First Blood Part II') { return 'Rambo: First Blood Part II' }
  return $t
}
function Res-Class($x) {
  if (-not $x.Width -or -not $x.Height) { return 'Unknown' }
  if ($x.Height -ge 2000 -or $x.Width -ge 3800) { return '4K-class' }
  if ($x.Height -ge 1000 -or $x.Width -ge 1900) { return '1080p-class' }
  if ($x.Height -ge 700 -or $x.Width -ge 1200) { return '720p-class' }
  return 'SD'
}
foreach ($x in $all) { $x.Title = Fix-Title $x.Title }
$movies = @($all | Where-Object MediaType -eq 'Movie' | Sort-Object Title, Year)
$shows = @($all | Where-Object MediaType -eq 'Show' | Sort-Object Series, Season, Episode)
$bonus = @($all | Where-Object MediaType -eq 'Bonus' | Sort-Object Title)

$movieRows = [System.Collections.ArrayList]::new(); [void]$movieRows.Add(@('Title','Year','Collection','Resolution','Duration (min)','Size (GB)','Video','Audio','Channels','Subtitle Status','Issues','File Name','Full Path'))
foreach($x in $movies){$issues=$x.Issues;if($x.Title -eq 'Sling Blade'){$issues=($issues+'; Duplicate title/year').Trim('; ')};[void]$movieRows.Add(@($x.Title,$x.Year,$x.Collection,"$($x.Width)x$($x.Height)",$x.DurationMinutes,$x.SizeGB,$x.VideoCodec,$x.AudioCodec,$x.AudioChannels,$x.SubtitleStatus,$issues,$x.FileName,$x.FullPath))}
$showRows = [System.Collections.ArrayList]::new(); [void]$showRows.Add(@('Series','Season','Episode','Resolution','Duration (min)','Size (GB)','Video','Audio','Channels','Subtitle Status','Issues','File Name','Full Path'))
foreach($x in $shows){[void]$showRows.Add(@($x.Series,$x.Season,$x.Episode,"$($x.Width)x$($x.Height)",$x.DurationMinutes,$x.SizeGB,$x.VideoCodec,$x.AudioCodec,$x.AudioChannels,$x.SubtitleStatus,$x.Issues,$x.FileName,$x.FullPath))}
$bonusRows = [System.Collections.ArrayList]::new(); [void]$bonusRows.Add(@('Title','Collection','Duration (min)','Size (GB)','Resolution','Subtitle Status','File Name','Full Path'))
foreach($x in $bonus){[void]$bonusRows.Add(@($x.Title,$x.Collection,$x.DurationMinutes,$x.SizeGB,"$($x.Width)x$($x.Height)",$x.SubtitleStatus,$x.FileName,$x.FullPath))}

$issues = [System.Collections.ArrayList]::new(); [void]$issues.Add(@('Severity','Category','Media','Title / Group','Details','Suggested Action','Path'))
foreach($x in $all | Where-Object SubtitleStatus -eq 'Missing English subtitle') {[void]$issues.Add(@('High','Subtitles',$x.MediaType,$x.Title,'No embedded or same-name English subtitle detected.','Find a release/hash-matched English subtitle, audio-sync it, then cache it.',$x.FullPath))}
$slings=@($movies|Where-Object Title -eq 'Sling Blade');[void]$issues.Add(@('Medium','Duplicate','Movie','Sling Blade (1996)',"$($slings.Count) copies: $($slings.FileName -join ' | ')",'Verify both, then retain the preferred edition.',$slings.FullPath -join ' | '))
[void]$issues.Add(@('High','Episode gap','Show',"Marvel's The Punisher - Season 1",'S01E08 is missing.','Add S01E08 and rescan.','C:\Users\clive\Videos\Shows\punisher'))
$gattaca=$movies|Where-Object Title -eq 'Gattaca'|Select-Object -First 1;[void]$issues.Add(@('Medium','Integrity review','Movie','Gattaca (1997)','Initial picture/keyframe probe warning.','Run a full decode verification before NAS archival.',$gattaca.FullPath))
$tagged=@($all|Where-Object Issues -match 'Release tags');[void]$issues.Add(@('Medium','NAS naming','Library','All media',"$($tagged.Count) filenames contain release/source/codec tags.",'Create a reversible Title (Year) / Show - SxxEyy rename plan.',''))
$lower=@($all|Where-Object {(Res-Class $_) -in @('720p-class','SD')});[void]$issues.Add(@('Medium','Resolution','Library','Lower-resolution inventory',"$($lower.Count) files are below 1080p-class.",'Treat as upgrade candidates; do not upscale archival masters.',''))
[void]$issues.Add(@('Low','Folder layout','Library','Shows split across roots','Shows are split between D:\shows and Windows Videos\Shows.','Consolidate under one NAS Shows root.',''))

$groups=$all|Group-Object MediaType,Collection|Sort-Object Name
$collections=[System.Collections.ArrayList]::new();[void]$collections.Add(@('Media Type','Collection / Group','Files','Size (GB)','Missing English Subs','Below 1080p-class','Files With Flags'))
foreach($g in $groups){$sample=$g.Group[0];[void]$collections.Add(@($sample.MediaType,$sample.Collection,$g.Count,[math]::Round(($g.Group|Measure-Object SizeGB -Sum).Sum,3),@($g.Group|Where-Object SubtitleStatus -eq 'Missing English subtitle').Count,@($g.Group|Where-Object {(Res-Class $_) -in @('720p-class','SD')}).Count,@($g.Group|Where-Object Issues).Count))}

$recs=@(
@('Priority','Type','Franchise / Theme','Suggested Addition','Why It Fits','Source URL'),
@('High','Show','Justified','Seasons 2–6; then City Primeval','Completes the series already started.','https://www.fxnetworks.com/shows/all'),
@('High','Show',"Marvel's The Punisher",'S01E08 and complete season 2','Closes the gap and completes the run.','https://www.marvel.com/tv-shows/marvel-s-the-punisher/2'),
@('High','Show','Silo','Season 3; track season 4','Continues the two-season set.','https://www.apple.com/tv-pr/originals/silo/'),
@('High','Movie','Bourne','The Bourne Legacy (2012)','Fills the main feature-film gap.',''),
@('High','Movie','Middle-earth','The Lord of the Rings extended trilogy','Natural companion to The Hobbit.',''),
@('Medium','Movie','Riddick','The Chronicles of Riddick: Dark Fury','Animated bridge film.',''),
@('High','Movies','Sci-fi franchises','Alien / Predator; Blade Runner; Matrix; Terminator; Mad Max','Strong match for the existing sci-fi library.',''),
@('High','Shows','Prestige sci-fi','The Expanse; Severance; Foundation; Dark; Fallout; Battlestar Galactica','Strong companions to Silo.',''),
@('High','Shows','Crime / action','Daredevil; Bosch; Reacher; Deadwood; Fargo; The Americans; The Shield','Strong companions to Justified and The Punisher.',''),
@('Medium','Movies','Comedy','Airplane!; Top Secret!; Hot Shots!; Austin Powers; Mel Brooks','Fits Police Squad!, Naked Gun, Monty Python, and Scary Movie.','')
)

$uniqueMovies=@($movies|Group-Object Title,Year).Count;$missing=@($all|Where-Object SubtitleStatus -eq 'Missing English subtitle');$storage=[math]::Round(($all|Measure-Object SizeGB -Sum).Sum,1)
$dash=@();1..30|ForEach-Object{$dash+=,@('','','','','','','','')};$dash[0][0]='Media Library Catalog';$dash[1][0]='Last scanned';$dash[1][1]=(Get-Date -Format 'yyyy-MM-dd');$dash[3][0]='Library Summary';$dash[3][1]='Value'
$metrics=@(@('Total video files',$all.Count),@('Movie files',$movies.Count),@('Unique movie titles',$uniqueMovies),@('TV episodes',$shows.Count),@('Bonus features',$bonus.Count),@('Storage used (GB)',$storage),@('Subtitle-ready files',($all.Count-$missing.Count)),@('Missing English subtitles',$missing.Count),@('Audio-synced subtitles',@($all|Where-Object SubtitleStatus -eq 'Audio-synced external').Count),@('Duplicate movie groups',1));for($i=0;$i-lt$metrics.Count;$i++){$dash[4+$i][0]=$metrics[$i][0];$dash[4+$i][1]=$metrics[$i][1]}
$dash[3][3]='Resolution Class';$dash[3][4]='Files';$rc=@($all|Group-Object {Res-Class $_}|Sort-Object Name);for($i=0;$i-lt$rc.Count;$i++){$dash[4+$i][3]=$rc[$i].Name;$dash[4+$i][4]=$rc[$i].Count}
$dash[16][0]='Priority Findings';$dash[16][1]='Count / Note';$find=@(@('Missing English subtitles',$missing.Count),@('Files below 1080p-class',$lower.Count),@('Release-tagged filenames',$tagged.Count),@('Missing episode','The Punisher S01E08'),@('Duplicate title','Sling Blade (1996)'),@('Integrity review','Gattaca (1997)'));for($i=0;$i-lt$find.Count;$i++){$dash[17+$i][0]=$find[$i][0];$dash[17+$i][1]=$find[$i][1]}
$dash[25][0]='Scope';$dash[25][1]='D:\Movies; D:\shows; Windows Videos\Movies; Windows Videos\Shows'

@{dashboard=$dash;movies=$movieRows;shows=$showRows;bonus=$bonusRows;issues=$issues;collections=$collections;recommendations=$recs} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $outDir 'sheet-data.json') -Encoding UTF8
Write-Output "Prepared: movies=$($movies.Count), shows=$($shows.Count), bonus=$($bonus.Count), issues=$($issues.Count-1), collections=$($collections.Count-1)"
