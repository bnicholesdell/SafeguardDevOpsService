import { Component, OnInit, ElementRef, ViewChild, Inject } from '@angular/core';
import { DevOpsServiceClient } from '../service-client.service';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { CreateCsrComponent } from '../create-csr/create-csr.component';
import * as $ from 'jquery';

@Component({
  selector: 'app-upload-certificate',
  templateUrl: './upload-certificate.component.html',
  styleUrls: ['./upload-certificate.component.scss']
})
export class UploadCertificateComponent implements OnInit {

  certificateType: string = '';

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: any,
    private serviceClient: DevOpsServiceClient,
    private dialog: MatDialog,
    private dialogRef: MatDialogRef<UploadCertificateComponent>) { }

  ngOnInit(): void {
    this.certificateType = this.data?.certificateType ?? '';
  }

  browse(): void {
    var fileInput = $('<input class="requestedCertInput" type="file" accept=".cer,.crt,.der,.pfx,.p12,.pem" />');

    fileInput.on('change',() => {
      const fileSelected = fileInput.prop('files')[0];
      
      if (!fileSelected) {
        return;
      }

      const fileReader = new FileReader();
      fileReader.onloadend = (e) => {
        let arrayBufferToString = (buffer) => {
          var binary = '';
          var bytes = new Uint8Array( buffer );
          var len = bytes.byteLength;
          for (var i = 0; i < len; i++) {
            binary += String.fromCharCode( bytes[ i ] );
          }
          return binary;
        };

        var pkcs12Der = arrayBufferToString(fileReader.result);
        let cert:string = btoa(pkcs12Der);
        this.dialogRef.close({
          fileType: fileSelected.type,
          fileContents: cert,
          fileName: fileSelected.name
        });
      };
      fileReader.readAsArrayBuffer(fileSelected);
    });
    $(".requestedCertInput").append(fileInput);
    fileInput.trigger("click");
  }

  createCSR(certificateType: string) {
    const dialogRef = this.dialog.open(CreateCsrComponent, {
      // disableClose: true
      data: {certificateType: certificateType}
    });

    dialogRef.afterClosed().subscribe(
      result => {
        if (result) {
        }
      }
    );
  }

}