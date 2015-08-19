using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace winsw
{
    /// <summary>
    /// Process the file copy instructions, so that we can replace files that are always in use while
    /// the service runs.
    /// <code><![CDATA[
    /// 
    /// Syntax:
    ///
    /// Paths will be left-right trimmed for whitespace.  Paths can be surrounded with quotes if they contain
    /// characters which are part of the "command set" below.  Quotes will be stripped automatically.
    ///
    ///   Comment (line starts with '#'):
    ///      # comment
    /// 
    ///   Copy (line contains '>' character):
    ///      source_path > dest_path
    ///
    ///      Note: the whitespace around '>' is optional.
    ///
    ///      If source_path and dest_path are files, source_path overwrites dest_path:
    /// 
    ///          tmp\folder\foo.txt > other\xyz.txt
    ///
    ///      If dest_path is a directory, source_path is copied into dest_path directory:
    /// 
    ///          tmp\foo.txt > folder
    /// 
    ///      If source_path is a folder, then:
    /// 
    ///          foo\bar > folder
    /// 
    ///          If 'folder' is an existing directory, the result will be a 'bar' directory inside
    ///          the 'folder' directory, i.e. 'folder\bar'.  If the goal is to replace (overwrite) 'folder',
    ///          you should use the delete file operation (below) to remove 'folder' before the copy
    ///          operation.
    ///
    ///          If 'folder' is not an existing directory, the result will be a that the 'bar' 
    ///          subdirectory is moved/renamed to 'folder'.
    ///
    ///      If source_path contains wildcard characters, all matching files are copied into dest_path folder:
    /// 
    ///          tmp\abc*.dll > other\folder
    ///
    ///   Copy without overwrite (line contains ')' ) character:
    ///
    ///      Functionally the same as copy above, except that existing files and/or directories will not
    ///      be overridden.
    /// 
    ///      In the case of a source directory copied to existing destination directory, the contents of
    ///      the source directory will be merged into the destination directory.
    ///
    ///   Delete (line starts with '<' character):
    ///      < target_path
    ///
    ///      If target_path is a file, it is deleted:
    /// 
    ///         < tmp\somefile.txt
    /// 
    ///      if target_path is a folder, it and its contents are deleted (recursively):
    ///
    ///         < tmp\folder
    /// 
    ///      If target_path contains wildcard characters, all matching files are deleted:
    /// 
    ///         < tmp\folder\abc*.dll
    ///
    ///   Execute (line starts with '@' character):
    ///      @executable arg1 arg2
    /// 
    /// ]]></code>
    /// </summary>
    class FileOperations
    {
        private readonly ServiceDescriptor _descriptor;
        private readonly WrapperService _wrapper;

        public FileOperations(WrapperService wrapper, ServiceDescriptor descriptor)
        {
            _descriptor = descriptor;
            _wrapper = wrapper;
        }

        internal void HandleFileCopies()
        {
            var file = _descriptor.BasePath + ".copies";

            if (!File.Exists(file))
                return; // nothing to handle

            try
            {
                using (var tr = new StreamReader(file, Encoding.UTF8))
                {
                    var lineNumber = 0;
                    string line;
                    while ((line = tr.ReadLine()) != null)
                    {
                        lineNumber++;
                        line = line.TrimStart();
                        if (line.Length == 0)
                            continue;

                        WriteEvent(string.Format("(%d) File operation: %s", lineNumber, line));

                        switch (line[0])
                        {
                            case '#': // comment
                                WriteEvent(line);
                                continue;
                            case '<': // delete
                                DeleteFileOrDirectory(CleanupFilename(line.Substring(1)));
                                break;
                            case '@': // execute
                                break;
                            default:
                                var moveOp = GetMoveOperation(line);
                                if (moveOp != null) // move
                                {
                                    MoveFileOrDir(moveOp[1], moveOp[2], moveOp[0].Equals("overwrite"));
                                }
                                else
                                {
                                    WriteEvent("Unknown file handling instruction: " + line);
                                    LogEvent("Unknown file handling instruction: " + line);
                                }
                                break;
                        }
                    }
                }
            }
            finally
            {
                File.Delete(file);
            }

        }

        /// <summary>
        /// Delete the specified file, wildcard files, or directory (recursively)
        /// </summary>
        /// <param name="target">the target file expression</param>
        private void DeleteFileOrDirectory(string target)
        {
            try
            {
                target = Path.Combine(_descriptor.WorkingDirectory, target);

                if (target.Contains("*") || target.Contains("?"))
                {
                    var parent = Directory.GetParent(target);
                    if (parent != null)
                    {
                        var match = target.Substring(target.Length - parent.Name.Length);
                        var files = Directory.GetFiles(parent.Name, match, SearchOption.TopDirectoryOnly);
                        foreach (var file in files)
                        {
                            LogEvent("Delete file: " + file);
                            File.Delete(file);
                        }
                    }
                }
                else if (Directory.Exists(target))
                {
                    WriteEvent("Delete directory recursively: " + target);
                    Directory.Delete(target, true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (IOException e)
            {
                LogEvent("Failed to delete: " + target + " because " + e.Message);
            }
        }

        /// <summary>
        /// File replacement.
        /// </summary>
        private void MoveFileOrDir(string sourceFileName, string destFileName, bool overwrite)
        {
            try
            {
                if (sourceFileName.Contains("*") || sourceFileName.Contains("?"))
                {
                    MoveWildcard(sourceFileName, destFileName, overwrite);
                }
                else if (Directory.Exists(sourceFileName))  // move DIRECTORY
                {
                    MoveDirectory(sourceFileName, destFileName, overwrite);
                }
                else // move FILE
                {
                    MoveFile(sourceFileName, destFileName, overwrite);
                }
            }
            catch (IOException e)
            {
                WriteEvent("Failed to rename/move: " + sourceFileName + " to " + destFileName + " because " + e.Message);
            }
        }

        private void MoveWildcard(string sourceFileName, string destFileName, bool overwrite)
        {
            var parent = Directory.GetParent(sourceFileName);
            if (parent != null && (Directory.Exists(destFileName) || Directory.CreateDirectory(destFileName) != null))
            {
                var search = sourceFileName.Substring(parent.FullName.Length + 1);
                foreach (var file in Directory.GetFiles(parent.FullName, search))
                {
                    WriteEvent("Recurse CopyFile(" + file + ", " + destFileName + ")");
                    MoveFileOrDir(file, destFileName, overwrite);
                }
            }
        }

        private void MoveDirectory(string sourceDirName, string destDirName, bool overwrite)
        {
            // string fullSourcePath = Path.GetFullPath(sourceFileName).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(sourceDirName);
            if (Directory.Exists(destDirName))
            {
                WriteEvent("Move directory " + sourceDirName + " into existing directory " + destDirName);
                destDirName = Path.Combine(destDirName, name);
                if (overwrite || !Directory.Exists(destDirName))
                {
                    if (Directory.Exists(destDirName))
                    {
                        Directory.Delete(destDirName, true);
                    }
                    Directory.Move(sourceDirName, destDirName);
                }
                else
                {
                    // copy/merge into destFileName directory and delete sourceFileName
                    foreach (var file in Directory.GetFiles(sourceDirName))
                    {
                        MoveFile(file, Path.Combine(destDirName, file), overwrite);
                    }

                    foreach (var dir in Directory.GetDirectories(sourceDirName))
                    {
                        MoveFileOrDir(dir, Path.Combine(destDirName, dir), overwrite);
                    }
                }
            }
            else
            {
                WriteEvent("Move/rename directory " + sourceDirName + " to directory " + destDirName);
                Directory.Move(sourceDirName, destDirName);
            }
        }

        private void MoveFile(string sourceFileName, string destName, bool overwrite)
        {
            if (Directory.Exists(destName))
            {
                destName = Path.Combine(destName, Path.GetFileName(sourceFileName));
            }

            if (overwrite || !File.Exists(destName))
            {
                WriteEvent("Move file " + sourceFileName + " into directory " + destName);
                File.Delete(destName);
                File.Move(sourceFileName, destName);
            }
            else
            {
                WriteEvent("Move file " + sourceFileName + " overwrite skipped");
            }
        }

        /// <summary>
        ///  This method determines if the string represents a file move operation.  It ignores move characters inside of
        ///  quotation marks.  Note however, it does not handle escaping quotation marks within a quoted string.
        /// </summary>
        private string[] GetMoveOperation(string line)
        {
            int gtCount = 0, gtPos = 0, prCount = 0, prPos = 0;
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                switch (line[i])
                {
                    case '"':
                        inQuote = !inQuote;
                        break;
                    case '>':
                        if (!inQuote)
                        {
                            gtPos = i;
                            gtCount++;
                        }
                        break;
                    case ')':
                        if (!inQuote)
                        {
                            prPos = i;
                            prCount++;
                        }
                        break;
                }
            }

            if (gtCount == 1)
            {
                var array = new string[3];
                array[0] = "overwrite";
                array[1] = CleanupFilename(line.Substring(0, gtPos - 1));
                array[2] = CleanupFilename(line.Substring(gtPos + 1));
                return array;
            }
            else if (prCount == 1)
            {
                var array = new string[3];
                array[0] = "no-overwrite";
                array[1] = CleanupFilename(line.Substring(0, prPos - 1));
                array[2] = CleanupFilename(line.Substring(prPos + 1));
                return array;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Remove leading/trailing whitespace and strip quotes if the string is quoted.
        /// </summary>
        /// <param name="name">The filename to cleanup.</param>
        /// <returns>The clean filename</returns>
        private string CleanupFilename(string name)
        {
            name = name.Trim();
            if (name.StartsWith("\"") && name.EndsWith("\""))
            {
                name = name.Substring(1, name.Length - 2);
            }

            return Environment.ExpandEnvironmentVariables(name);
        }

        private void WriteEvent(string message)
        {
            _wrapper.WriteEvent(message);
        }

        private void LogEvent(string message)
        {
            _wrapper.LogEvent(message);
        }
    }
}
