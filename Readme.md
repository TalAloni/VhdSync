About VhdSync:
==============
VhdSync is designed to perform one-way synchronization between large files in a way that reduce disk writes and reduce the time the target file is inconsistent.  
The contents of the source and target files are compared, and then the segments that are different are copied to the target.
I wrote it as a method of updating my backups of Vitual Hard Drives (VHD and VMDK files), but it can be used with other files as well.  
VhdSync is free and its source code is licensed under LGPL 3.0.  

What is block-level sync?
=========================
Block-level sync (a.k.a. bit-level file synchronization) means that only the parts of a file that have changed are being copied, rather than the entire file.  
Given that solid-state drives (SSD) have a limited number of writes (or erasures) before they wear out, reducing the number of writes will increase the drive longevity.  

Technical Notes:
================
* Currently this program only sync files, not directories!  
* The size of the source and target files must be identical.  
* During sync the file is segmented to 1 MB segments, if a change is detected between source and target in a given segment, the entire segment is copied.  

Using VhdSync:
==============
VhdSync is a command line application.  
VhdSync &lt;Source-File-Path&gt; &lt;Target-File-Path&gt;  

Contact:
========
This program is provided as-is, I do not intend to improve it for your needs or provide support.  
Please do not contact me for those purposes.  
