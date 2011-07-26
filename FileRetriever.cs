// Copyright © Microsoft Corporation.  All Rights Reserved.
// This code released under the terms of the
// Apache License, Version 2.0 (http://opensource.org/licenses/Apache-2.0)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Synchronization;
using Microsoft.Synchronization.Files;

namespace FileSystemDurabilityPlugin
{
    class FileRetriever : IFileDataRetriever
    {
        FileData _fileData;
        Stream _fileStream;
        string _relativePath;


        internal FileRetriever(FileData fileData, string relativePath, Stream fileStream)
        {
            if (fileData == null)
            {
                throw new ApplicationException("Application Bug");
            }

            if (relativePath == null)
            {
                relativePath = "";
            }

            _fileData = fileData;
            _fileStream = fileStream;
            _relativePath = relativePath;
        }


        #region IFileDataRetriever Members

        public string AbsoluteSourceFilePath
        {
            get 
            {
                throw new NotImplementedException("Absolute Path Not Supported");
            }
        }

        public FileData FileData
        {
            get 
            {
                return _fileData;
            }
        }

        public System.IO.Stream FileStream
        {
            get
            {
                return _fileStream;
            }
        }

        public string RelativeDirectoryPath
        {
            get 
            {
                return _relativePath;
            }
        }

        #endregion
    }
}
